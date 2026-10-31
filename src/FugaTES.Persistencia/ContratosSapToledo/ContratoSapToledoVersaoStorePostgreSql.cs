using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using Npgsql;
using NpgsqlTypes;
namespace FugaTES.Persistencia.ContratosSapToledo;
public sealed class ContratoSapToledoVersaoStorePostgreSql(NpgsqlDataSource dataSource) : IContratoSapToledoAdmissionStore
{
    public async Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,string fingerprint,string payload,string correlationId,CancellationToken ct)
        => await RegistrarAsync(snapshot, fingerprint, payload, correlationId, null, ct);

    public async Task<VersaoContratoSapToledo> RegistrarPrimeiroOuObterReentregaAsync(
        ContratoSapToledoSnapshot snapshot, string fingerprint, string payload, string correlationId,
        AcmInboundAdmission admission, CancellationToken ct)
        => await RegistrarAsync(snapshot, fingerprint, payload, correlationId, admission, ct);

    private async Task<VersaoContratoSapToledo> RegistrarAsync(ContratoSapToledoSnapshot snapshot,string fingerprint,string payload,string correlationId,AcmInboundAdmission? admission,CancellationToken ct)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct); await using var tx=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try {
            if (admission is not null)
            {
                if (!admission.EhValida()) throw new AcmInboundScopeConflictException();
                // Serialize the first admission across different contract numbers in this authorization.
                // The lock, core integration and H4 version use the SAME physical transaction.
                await using (var windowLock = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@window,0));", connection, tx))
                {
                    windowLock.Parameters.AddWithValue("window", admission.CorrelationPrefix);
                    await windowLock.ExecuteNonQueryAsync(ct);
                }
                var previous = AcmInboundAdmissionGuard.Resolver(admission, snapshot, fingerprint, correlationId,
                    await DatabaseNowAsync(connection, tx, ct), await AdmittedRowsAsync(connection, tx, admission, ct));
                if (previous is not null) { await tx.CommitAsync(ct); return previous; }
                var canonical = new ContratoSapToledoCanonicalizer().Canonicalizar(snapshot);
                if (canonical.PayloadCanonicoJson != payload) throw new AcmInboundScopeConflictException();
            }
            await using(var ensure=new NpgsqlCommand("insert into contrato_sap_toledo_identidade(numero_contrato) values(@numero) on conflict(numero_contrato) do nothing;",connection,tx)){ensure.Parameters.AddWithValue("numero",snapshot.NumeroContrato);var inserted=await ensure.ExecuteNonQueryAsync(ct);if(admission is not null && inserted!=1)throw new AcmInboundScopeConflictException();}
            await using(var locked=new NpgsqlCommand("select ultima_versao from contrato_sap_toledo_identidade where numero_contrato=@numero for update;",connection,tx)){locked.Parameters.AddWithValue("numero",snapshot.NumeroContrato);var last=Convert.ToInt64(await locked.ExecuteScalarAsync(ct));
                await using var find=new NpgsqlCommand("select versao,integracao_id,payload_contrato::text from contrato_sap_toledo_versao where numero_contrato=@numero and fingerprint_sha256=@fingerprint;",connection,tx);find.Parameters.AddWithValue("numero",snapshot.NumeroContrato);find.Parameters.AddWithValue("fingerprint",fingerprint);await using var reader=await find.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){var version=reader.GetInt64(0);var id=reader.GetGuid(1);var json=reader.GetString(2);await reader.DisposeAsync();await tx.CommitAsync(ct);return new(ResultadoRegistroVersaoContrato.JaExistente,snapshot.NumeroContrato,version,fingerprint,id,JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(json)!);}await reader.DisposeAsync();
                var idNew=Guid.NewGuid();var versionNew=last+1;var candidate=new CandidatoIntegracao("SAP","TOLEDO","SAP_CONTRATO_TOLEDO",snapshot.NumeroContrato,fingerprint,correlationId);var registered=await RepositorioMotorIntegracaoPostgreSql.RegistrarCandidatoNaTransacaoAsync(connection,tx,candidate,idNew,ct);if(registered!=ResultadoRegistroIntegracao.Criada)throw new InvalidOperationException("H4 integration fingerprint already exists without matching version.");
                await using(var insert=new NpgsqlCommand("insert into contrato_sap_toledo_versao(numero_contrato,versao,fingerprint_sha256,payload_contrato,integracao_id) values(@numero,@versao,@fingerprint,@payload,@id);",connection,tx)){insert.Parameters.AddWithValue("numero",snapshot.NumeroContrato);insert.Parameters.AddWithValue("versao",versionNew);insert.Parameters.AddWithValue("fingerprint",fingerprint);insert.Parameters.AddWithValue("payload",NpgsqlDbType.Jsonb,payload);insert.Parameters.AddWithValue("id",idNew);await insert.ExecuteNonQueryAsync(ct);}
                await using(var update=new NpgsqlCommand("update contrato_sap_toledo_identidade set ultima_versao=@versao where numero_contrato=@numero;",connection,tx)){update.Parameters.AddWithValue("versao",versionNew);update.Parameters.AddWithValue("numero",snapshot.NumeroContrato);await update.ExecuteNonQueryAsync(ct);}
                if(admission is not null && !admission.EstaAberta(await DatabaseNowAsync(connection,tx,ct)))throw new AcmInboundScopeConflictException();
                await tx.CommitAsync(ct);return new(ResultadoRegistroVersaoContrato.Criada,snapshot.NumeroContrato,versionNew,fingerprint,idNew,snapshot){AdmissionCorrelationId=admission is null?null:correlationId};
            }
        } catch { await tx.RollbackAsync(ct); throw; }
    }
    private static async Task<DateTimeOffset> DatabaseNowAsync(NpgsqlConnection connection,NpgsqlTransaction tx,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("select clock_timestamp();",connection,tx);
        return new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync(ct))!);
    }

    private static async Task<IReadOnlyList<AcmInboundAdmittedRow>> AdmittedRowsAsync(
        NpgsqlConnection connection,NpgsqlTransaction tx,AcmInboundAdmission admission,CancellationToken ct)
    {
        const string sql="""
            select i.id,i.correlation_id,i.criado_em,i.origem,i.destino,i.operacao,i.chave_negocio,i.fingerprint_sha256,
                   v.numero_contrato,v.versao,v.fingerprint_sha256,v.payload_contrato::text
            from integracao i left join contrato_sap_toledo_versao v on v.integracao_id=i.id
            where left(i.correlation_id,length(@prefix))=@prefix
            order by i.criado_em,i.id limit 2;
            """;
        await using var cmd=new NpgsqlCommand(sql,connection,tx);cmd.Parameters.AddWithValue("prefix",admission.CorrelationPrefix);
        await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<AcmInboundAdmittedRow>(2);
        while(await r.ReadAsync(ct))
        {
            VersaoContratoSapToledo? version=null;
            if(!r.IsDBNull(8))
                version=new(ResultadoRegistroVersaoContrato.JaExistente,r.GetString(8),r.GetInt64(9),r.GetString(10),r.GetGuid(0),
                    JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(r.GetString(11))!);
            rows.Add(new(version,r.GetString(1),new DateTimeOffset(r.GetDateTime(2)),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7)));
        }
        return rows;
    }
    public async Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct){const string sql="select numero_contrato,versao,fingerprint_sha256,payload_contrato::text,criado_em from contrato_sap_toledo_versao where integracao_id=@id;";await using var cmd=dataSource.CreateCommand(sql);cmd.Parameters.AddWithValue("id",id);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(ResultadoRegistroVersaoContrato.JaExistente,r.GetString(0),r.GetInt64(1),r.GetString(2),id,JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(r.GetString(3))!){RecebidoEmUtc=r.GetFieldValue<DateTimeOffset>(4)}:null;}
    public async Task<bool> TemPredecessorNaoTerminalAsync(string numero,long versao,CancellationToken ct){const string sql="select exists(select 1 from contrato_sap_toledo_versao v join integracao i on i.id=v.integracao_id where v.numero_contrato=@numero and v.versao<@versao and i.estado in ('PENDENTE','EM_PROCESSAMENTO','AGUARDANDO_REPROCESSAMENTO','AGUARDANDO_ACAO_MANUAL','RESULTADO_INDETERMINADO'));";await using var cmd=dataSource.CreateCommand(sql);cmd.Parameters.AddWithValue("numero",numero);cmd.Parameters.AddWithValue("versao",versao);return (bool)(await cmd.ExecuteScalarAsync(ct)??false);}
}
