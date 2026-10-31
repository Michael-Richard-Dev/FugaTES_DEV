using System.Text.Json;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Recebimento;
using Npgsql;
namespace FugaTES.Persistencia.Recebimento;

public sealed class DevolutivaToledoSapPostgreSql(NpgsqlDataSource source,ToledoSapAdmissionWindow? window=null) : IDevolutivaToledoSapStore
{
    public async Task<DocumentoToledoSap?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
    {
        const string sql = """
        select r.recibo_id,r.centro,r.id_pesagem,r.documento_json::text,r.fingerprint_sha256,
          exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id and a.evento='toledo_sap_admitido')
          and exists(select 1 from fugates.toledo_pesagem_item f where f.recibo_id=r.recibo_id)
          and ((@complete and exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id
                and a.evento='toledo_sap_documento_completo_admitido' and a.diagnostico=@reference))
            or not exists(select 1 from fugates.toledo_pesagem_item f where f.recibo_id=r.recibo_id and
             (f.estado_correlacao<>'H4_MATCHED' or f.integracao_id is null or f.versao_h4 is null))),
          exists(select 1 from fugates.toledo_pesagem_item f where f.recibo_id=r.recibo_id and ltrim(f.contrato_acm,'0')='986801'),r.recebido_em
        from fugates.integracao i join fugates.toledo_pesagem_recebida r on r.recibo_id::text=i.chave_negocio
        where i.id=@id and i.operacao=@operation and i.origem='GUARDIAN' and i.destino='SAP'
          and i.tentativa_atual_id=@attempt and i.fencing_token=@fence and i.worker_proprietario=@worker
          and i.estado='EM_PROCESSAMENTO' and i.lease_expira_em>clock_timestamp();
        """;
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        try
        {
            await using(var protect=new NpgsqlCommand("set transaction read only;set local statement_timeout='5s';set local lock_timeout='1s';",c,tx))await protect.ExecuteNonQueryAsync(ct);
            await using var command=new NpgsqlCommand(sql,c,tx);Args(command,claim);
            command.Parameters.AddWithValue("complete",window?.DocumentoCompletoAprovado==true);command.Parameters.AddWithValue("reference",window?.ReferenciaAutorizacao??"");
            await using var read=await command.ExecuteReaderAsync(ct);if(!await read.ReadAsync(ct))return null;
            var document=new DocumentoToledoSap(read.GetGuid(0),read.GetString(1),read.GetString(2),read.GetString(3),read.GetString(4),read.GetBoolean(5),read.GetBoolean(6)){RecebidoEm=read.GetFieldValue<DateTimeOffset>(7)};
            await read.DisposeAsync();
            return document with{DeliveryPartners=await ToledoDeliveryPartnerRead.ReadAsync(c,tx,claim.IntegracaoId,false,ct),ContratosSap=await ToledoSapContractPartnerRead.ReadAsync(c,tx,document.DocumentoJson,document.RecebidoEm,ct)};
        }
        finally{await tx.RollbackAsync(CancellationToken.None);}
    }
    public Task<bool> ValidarDespachoAsync(ClaimIntegracaoMotor claim,PlanoEnvioToledoSap plano,CancellationToken ct)
        =>plano.Valido&&plano.ParceirosComprovados&&plano.DeliveryPartners is not null&&window?.Valida==true?Audit(claim,"toledo_sap_documento_preparado",JsonSerializer.Serialize(plano),false,ct,true):Task.FromResult(false);
    public async Task<bool> MarcarDespachoAsync(ClaimIntegracaoMotor claim,CancellationToken ct)
        =>await Audit(claim,"toledo_sap_envio_iniciado",null,true,ct);
    public async Task RegistrarRetornoAsync(ClaimIntegracaoMotor claim,RetornoToledoSap result,CancellationToken ct)
    {
        if(!Enum.IsDefined(result.Codigo)||result.HttpStatus is <100 or >599)throw new InvalidOperationException("TOLEDO_SAP_INVALID_OUTCOME");
        var safe=JsonSerializer.Serialize(new{codigo=result.Codigo.ToString(),httpStatus=result.HttpStatus,postIniciado=result.PostIniciado,entityId=result.EntityId,requestSha256=result.RequestSha256,responseSha256=result.ResponseSha256,payloadVersion=result.PayloadVersion,detailCode=result.DetailCode});
        if(!await Audit(claim,"toledo_sap_resultado",safe,false,ct))throw new InvalidOperationException("TOLEDO_SAP_OUTCOME_NOT_PERSISTED");
    }
    async Task<bool> Audit(ClaimIntegracaoMotor claim,string evt,string? diagnostic,bool singleIntent,CancellationToken ct,bool requireWindow=false)
    {
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        try
        {
            await using(var setup=new NpgsqlCommand("set local statement_timeout='5s';set local lock_timeout='1s';",c,tx))await setup.ExecuteNonQueryAsync(ct);
            const string guard="""
            select i.id from fugates.integracao i where i.id=@id and i.operacao=@operation and i.origem='GUARDIAN' and i.destino='SAP'
             and i.tentativa_atual_id=@attempt and i.fencing_token=@fence and i.worker_proprietario=@worker
             and i.estado='EM_PROCESSAMENTO' and i.fase_envio='ENVIO_INICIADO' and i.lease_expira_em>clock_timestamp()
             and (not @window or (clock_timestamp()>=@start and (@continuous or clock_timestamp()<@end))) for update;
            """;
            await using(var locked=new NpgsqlCommand(guard,c,tx)){Args(locked,claim);locked.Parameters.AddWithValue("window",requireWindow);locked.Parameters.AddWithValue("start",window?.InicioUtc??DateTimeOffset.MinValue);locked.Parameters.AddWithValue("end",window?.ContinuoAutorizado==true?window.InicioUtc:window?.FimUtc??DateTimeOffset.MinValue);locked.Parameters.AddWithValue("continuous",window?.ContinuoAutorizado==true);if(await locked.ExecuteScalarAsync(ct) is null){await tx.RollbackAsync(ct);return false;}}
            if(singleIntent)
            {
                await using var previous=new NpgsqlCommand("select exists(select 1 from fugates.auditoria_integracao where integracao_id=@id and tentativa_id=@attempt and evento='toledo_sap_envio_iniciado');",c,tx);
                previous.Parameters.AddWithValue("id",claim.IntegracaoId);previous.Parameters.AddWithValue("attempt",claim.TentativaId);
                if(await previous.ExecuteScalarAsync(ct) is true){await tx.RollbackAsync(ct);return false;}
            }
            const string insert="""
            insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,worker,fencing_token,tentativa_id,correlation_id,diagnostico)
            values (@audit,@id,@event,clock_timestamp(),@worker,@fence,@attempt,@correlation,@diagnostic);
            """;
            await using(var audit=new NpgsqlCommand(insert,c,tx))
            {Args(audit,claim);audit.Parameters.AddWithValue("audit",Guid.NewGuid());audit.Parameters.AddWithValue("event",evt);audit.Parameters.AddWithValue("correlation",claim.CorrelationId);audit.Parameters.AddWithValue("diagnostic",NpgsqlTypes.NpgsqlDbType.Text,(object?)diagnostic??DBNull.Value);if(await audit.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("TOLEDO_SAP_AUDIT_FAILED");}
            await tx.CommitAsync(ct);return true;
        }
        catch{try{await tx.RollbackAsync(CancellationToken.None);}catch{}throw;}
    }
    static void Args(NpgsqlCommand cmd,ClaimIntegracaoMotor c)
    {cmd.CommandTimeout=6;cmd.Parameters.AddWithValue("id",c.IntegracaoId);cmd.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);cmd.Parameters.AddWithValue("attempt",c.TentativaId);cmd.Parameters.AddWithValue("fence",c.FencingToken);cmd.Parameters.AddWithValue("worker",c.Worker);}
}
