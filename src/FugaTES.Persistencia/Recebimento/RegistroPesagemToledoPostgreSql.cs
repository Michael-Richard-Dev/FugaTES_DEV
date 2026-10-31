using System.Data;
using System.Data.Common;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using Npgsql;
using NpgsqlTypes;

namespace FugaTES.Persistencia.Recebimento;

// The existing inbox and INSERT/ON CONFLICT/readback/COMMIT sequence are retained.
// Production composition supplies only the dedicated, physically guarded Npgsql datasource.
// DbDataSource allows transaction failure tests without opening any PostgreSQL connection.
public sealed class RegistroPesagemToledoPostgreSql(DbDataSource source, ToledoSapAdmissionWindow? sapAdmission = null):IRegistroPesagemToledoStore
{
    const string InsertSql="""
        insert into fugates.toledo_pesagem_recebida
          (recibo_id, origem, centro, id_pesagem, data_pesagem_local, documento_json, fingerprint_sha256, estado, recebido_em, origem_autenticada)
        values (@id, @origem, @centro, @pesagem, @data, cast(@documento as jsonb), @fingerprint, 'RECEIVED_PENDING_CORRELATION', now(), @actor)
        on conflict (origem, centro, id_pesagem) do nothing returning recibo_id, recebido_em;
        """;
    const string SelectSql="""
        select recibo_id, recebido_em, fingerprint_sha256 from fugates.toledo_pesagem_recebida
        where origem=@origem and centro=@centro and id_pesagem=@pesagem;
        """;
    const string H4Sql="""
        select v.integracao_id,v.versao,v.fingerprint_sha256,i.fingerprint_sha256,v.payload_contrato::text,i.correlation_id
        from fugates.contrato_sap_toledo_versao v join fugates.integracao i on i.id=v.integracao_id
        where v.numero_contrato=@contrato and v.payload_contrato->>'NumeroContrato'=@contrato
          and v.payload_contrato->>'Centro'=@centro and v.payload_contrato->>'Material'=@material
          and v.payload_contrato->>'IdParceiro'=@parceiro
          and i.origem='SAP' and i.destino='TOLEDO' and i.operacao='SAP_CONTRATO_TOLEDO'
          and i.chave_negocio=v.numero_contrato
        order by v.versao limit 2;
        """;
    const string ItemSql="""
        insert into fugates.toledo_pesagem_item
          (recibo_id,indice,nr_indice_total,contrato_acm,id_parceiro,documento_item,integracao_id,versao_h4,estado_correlacao)
        values (@id,@indice,@total,@contrato,@parceiro,cast(@documento as jsonb),@integracao,@versao,@estado);
        """;
    const string ItemsReadSql="""
        select indice,estado_correlacao,integracao_id,versao_h4 from fugates.toledo_pesagem_item where recibo_id=@id order by indice;
        """;
    const string AuditSql="""
        insert into fugates.toledo_pesagem_entrega
          (entrega_id,recibo_id,origem_autenticada,recebido_em,resultado)
        values (@entrega,@id,@actor,now(),@resultado);
        """;
    public async Task<bool> JaRecebidoAsync(PesagemToledoRecebida doc,string fingerprint,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try
        {
            await Run(c,tx,"set local transaction_read_only=on; set local statement_timeout='5s'; set local lock_timeout='1s';",ct);
            await using var cmd=Command(c,tx,SelectSql,("origem",doc.Origem),("centro",doc.Centro),("pesagem",doc.IdPesagem));
            await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return false;
            if(r.GetString(2)!=fingerprint)throw new PesagemToledoConflitoException();return true;
        }
        finally{await tx.RollbackAsync(CancellationToken.None);}
    }
    public async Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida doc,string fingerprint,CancellationToken ct)
    {
        if(doc.Itens.Count is <1 or >1000||string.IsNullOrWhiteSpace(doc.Material)||string.IsNullOrWhiteSpace(doc.OrigemAutenticada))throw new PesagemToledoStorageException();
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(30));ct=limit.Token;
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try
        {
            await Run(connection,tx,"set local lock_timeout='1s'; set local statement_timeout='5s';",ct);
            ReciboPesagemToledo? receipt=null;
            await using(var insert=Command(connection,tx,InsertSql,("id",Guid.NewGuid()),("origem",doc.Origem),("centro",doc.Centro),("pesagem",doc.IdPesagem),
                ("data",doc.DataPesagemLocal),("documento",doc.DocumentoJson),("fingerprint",fingerprint),("actor",doc.OrigemAutenticada)))
            {
                await using var reader=await insert.ExecuteReaderAsync(ct);
                if(await reader.ReadAsync(ct))receipt=new(reader.GetGuid(0),Utc(reader.GetDateTime(1)),false);
            }
            var items=new List<CorrelacaoItemPesagemToledo>();
            if(receipt is null)
            {
                // READ COMMITTED gets a fresh snapshot after a competing INSERT has committed.
                await using(var select=Command(connection,tx,SelectSql,("origem",doc.Origem),("centro",doc.Centro),("pesagem",doc.IdPesagem)))
                {
                    await using var reader=await select.ExecuteReaderAsync(ct);
                    if(!await reader.ReadAsync(ct))throw new PesagemToledoStorageException();
                    if(reader.GetString(2)!=fingerprint)throw new PesagemToledoConflitoException();
                    receipt=new(reader.GetGuid(0),Utc(reader.GetDateTime(1)),true);
                }
                await using(var read=Command(connection,tx,ItemsReadSql,("id",receipt.ReciboId)))
                {
                    await using var reader=await read.ExecuteReaderAsync(ct);
                    while(await reader.ReadAsync(ct))items.Add(new(reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.IsDBNull(3)?null:reader.GetInt64(3)));
                }
                // Do not rewrite or re-correlate a previous receipt on redelivery.
            }
            else foreach(var item in doc.Itens)
            {
                var correlation=await Correlate(connection,tx,doc,item,ct);items.Add(correlation);
                await Run(connection,tx,ItemSql,ct,("id",receipt.ReciboId),("indice",item.Indice),("total",item.NrIndiceTotal),("contrato",item.ContratoACM),
                    ("parceiro",item.IdParceiro),("documento",item.DocumentoJson),("integracao",correlation.IntegracaoId),("versao",correlation.VersaoH4),("estado",correlation.Estado));
            }
            await Run(connection,tx,AuditSql,ct,("entrega",Guid.NewGuid()),("id",receipt.ReciboId),("actor",doc.OrigemAutenticada),("resultado",receipt.Idempotente?"IDEMPOTENT":"NEW_RECEIPT"));
            Guid? sapIntegration = null;
            // Admission and inbox share the SAME transaction. Redelivery never creates outbound work.
            // H4 failures remain visible receipt blockers, not fabricated correlation or a new ticket identity.
            if (!receipt.Idempotente && sapAdmission?.Admite(receipt.RecebidoEm, doc.OrigemAutenticada) == true &&
                items.Count == doc.Itens.Count && !doc.Itens.Any(i=>i.ContratoACM.TrimStart('0')=="986801") &&
                (sapAdmission.DocumentoCompletoAprovado || items.All(i => i.Estado == "H4_MATCHED")))
            {
                if (connection is not NpgsqlConnection nc || tx is not NpgsqlTransaction nt)
                    throw new PesagemToledoStorageException();
                sapIntegration = Guid.NewGuid();
                var candidate = ToledoSapWork.Candidato(receipt.ReciboId, fingerprint);
                if (await RepositorioMotorIntegracaoPostgreSql.RegistrarCandidatoNaTransacaoAsync(nc, nt, candidate, sapIntegration.Value, ct)
                    != FugaTES.Aplicacao.Modelos.MotorIntegracao.ResultadoRegistroIntegracao.Criada)
                    throw new PesagemToledoStorageException();
                await Run(connection,tx,"""
                    insert into fugates.auditoria_integracao
                      (id,integracao_id,evento,criada_em,correlation_id,diagnostico)
                    values (@audit,@integration,'toledo_sap_admitido',clock_timestamp(),@correlation,@proof);
                    """,ct,("audit",Guid.NewGuid()),("integration",sapIntegration.Value),("correlation",candidate.CorrelationId),
                    ("proof",sapAdmission.ReferenciaAutorizacao));
                if(sapAdmission.DocumentoCompletoAprovado)
                    await Run(connection,tx,"""
                        insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,correlation_id,diagnostico)
                        values(@audit,@integration,'toledo_sap_documento_completo_admitido',clock_timestamp(),@correlation,@proof);
                        """,ct,("audit",Guid.NewGuid()),("integration",sapIntegration.Value),("correlation",candidate.CorrelationId),("proof",sapAdmission.ReferenciaAutorizacao));
            }
            if(receipt.Idempotente && sapAdmission is not null)
            {
                await using var existing=Command(connection,tx,"""
                    select id from fugates.integracao where origem='GUARDIAN' and destino='SAP'
                     and operacao=@operation and chave_negocio=@receipt and fingerprint_sha256=@workhash limit 2;
                    """,("operation",ToledoSapWork.Operacao),("receipt",receipt.ReciboId.ToString("D")),
                    ("workhash",ToledoSapWork.Fingerprint(receipt.ReciboId,fingerprint)));
                await using var read=await existing.ExecuteReaderAsync(ct);
                if(await read.ReadAsync(ct))sapIntegration=read.GetGuid(0);
                if(await read.ReadAsync(ct))throw new PesagemToledoStorageException();
            }
            if(!receipt.Idempotente)
            {
                _=ToledoDeliveryPartners.Apply(doc.DocumentoJson,doc.DeliveryPartners);
                await Run(connection,tx,"""
                    insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,correlation_id,diagnostico)
                    values(@audit,@integration,@event,clock_timestamp(),@correlation,@proof);
                    """,ct,("audit",Guid.NewGuid()),("integration",sapIntegration),("event",ToledoDeliveryPartners.Event),
                    ("correlation",$"toledo-sap:{receipt.ReciboId:D}"),("proof",JsonSerializer.Serialize(doc.DeliveryPartners)));
            }
            await tx.CommitAsync(ct); // HTTP ACK is forbidden before this returns successfully.
            return receipt with{Itens=items,SapIntegracaoId=sapIntegration};
        }
        catch
        {
            try{await tx.RollbackAsync(CancellationToken.None);}catch{/* An unknown COMMIT never authorizes ACK/retry. */}
            throw;
        }
    }
    static DateTimeOffset Utc(DateTime value)=>value.Kind==DateTimeKind.Utc?new(value):throw new PesagemToledoStorageException();
    static async Task<CorrelacaoItemPesagemToledo> Correlate(DbConnection c,DbTransaction tx,PesagemToledoRecebida doc,ItemPesagemToledoRecebido item,CancellationToken ct)
    {
        CorrelacaoItemPesagemToledo Pending(string state)=>new(item.Indice,state);
        if(item.ContratoACM.TrimStart('0')=="986801")return Pending("H4_UNVERIFIED");
        await using var cmd=Command(c,tx,H4Sql,("contrato",item.ContratoACM),("centro",doc.Centro),("material",doc.Material),("parceiro",item.IdParceiro));
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return Pending("H4_NOT_FOUND");
        var id=reader.GetGuid(0);var version=reader.GetInt64(1);var fingerprint=reader.GetString(2);var integrationFingerprint=reader.GetString(3);
        var json=reader.GetString(4);var correlation=reader.IsDBNull(5)?"":reader.GetString(5);
        if(await reader.ReadAsync(ct))return Pending("H4_AMBIGUOUS");
        try
        {
            var s=JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(json)??throw new InvalidOperationException();
            if(id==Guid.Empty||id==Guid.Parse("e502b0a0-57c5-49f3-976c-9e1fe7e355e3")||version<=0||string.IsNullOrWhiteSpace(correlation)||
                fingerprint!=integrationFingerprint||new ContratoSapToledoCanonicalizer().Canonicalizar(s).FingerprintSha256!=fingerprint||
                s.NumeroContrato!=item.ContratoACM||s.Centro!=doc.Centro||s.Material!=doc.Material||s.IdParceiro!=item.IdParceiro)
                return Pending("H4_UNVERIFIED");
            // H4 match is not proof of Ticket.Codigo, IdAgendamento/protocol equivalence or SAP dispatch authority.
            return new(item.Indice,"H4_MATCHED",id,version);
        }
        catch{return Pending("H4_UNVERIFIED");}
    }
    static DbCommand Command(DbConnection c,DbTransaction tx,string sql,params (string Name,object? Value)[] args)
    {
        var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;cmd.CommandTimeout=5;
        foreach(var (name,value) in args)
        {
            var p=cmd.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;
            if(name is "integracao" or "integration")p.DbType=DbType.Guid;
            if(name=="versao")p.DbType=DbType.Int64;
            if(value is DateTime&&p is NpgsqlParameter pg)pg.NpgsqlDbType=NpgsqlDbType.Timestamp;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
    static async Task Run(DbConnection c,DbTransaction tx,string sql,CancellationToken ct,params (string Name,object? Value)[] args)
    {
        await using var cmd=Command(c,tx,sql,args);var changed=await cmd.ExecuteNonQueryAsync(ct);
        if(!sql.StartsWith("set local",StringComparison.Ordinal)&&changed!=1)throw new PesagemToledoStorageException();
    }
}
