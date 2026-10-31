using System.Text.Json;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.ContratosAcm;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Persistencia.Transportadoras;
using Npgsql;

namespace FugaTES.Persistencia.Consultas.UnifiedDev;

public sealed record DevDatabaseIdentity(string Cluster,int Port,string Database,string Role,bool ReadOnly,DateTimeOffset CapturedUtc)
{
    // Sem argumento: aceita qualquer uma das identidades de LEITURA autorizadas. Usado pelo cliente
    // (DEV110), que recebe a identidade pela API e nao sabe qual das duas o servidor foi configurado
    // para usar. A role de escrita do receptor SAP nunca esta nessa lista.
    public void Validate()
        => Validate(MonitorPostgreSqlReadRepository.RolesDeLeituraAutorizadas);

    // Com a role esperada: usado pelo servidor, que sabe exatamente qual identidade configurou.
    public void Validate(string roleEsperada)
        => Validate([roleEsperada]);

    void Validate(string[] rolesAceitas)
    {
        if(Cluster!="18/fugatesacmdev"||Port!=5433||Database!="fugates_dev"||!ReadOnly||
            !rolesAceitas.Contains(Role,StringComparer.Ordinal)||
            DateTimeOffset.UtcNow-CapturedUtc>TimeSpan.FromSeconds(15)||CapturedUtc>DateTimeOffset.UtcNow.AddSeconds(5))
            throw new InvalidOperationException("UNIFIED_DEV_DATABASE_IDENTITY_REJECTED");
    }
}
public sealed record DevReadEnvelope<T>(DevDatabaseIdentity Identity,T Data);
public sealed record DevMasterdataSnapshot(string Schema,long Total,IReadOnlyList<CarrierCreateHistoryEntry> Rows);
public sealed record DevProtocolView(MonitorIntegracao Integration,MonitorAcmDados Acm,bool KnownSynthetic,string Origin);
public static class DevReadScope
{
    public static readonly Guid PilotId=Guid.Parse("e502b0a0-57c5-49f3-976c-9e1fe7e355e3");
    public static string Classify(Guid id,string? contract)=>id==PilotId&&contract=="0000986801"?
        "SIMULACAO_CONFIRMADA":"PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA";
}
public interface IDevUnifiedReadStore
{
    Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct);
    Task<object> ReadAsync(string resource,string? selector,CancellationToken ct);
}

// Facade over the canonical repositories. One application datasource, GET-only consumers, no writes.
// roleEsperada e a identidade com que a API foi configurada. Nula mantem o comportamento historico
// (role ACM compartilhada). O valor e validado contra a lista branca de leitura.
public sealed class DevUnifiedReadStore(NpgsqlDataSource source,string? roleEsperada=null):IDevUnifiedReadStore
{
    readonly MonitorPostgreSqlReadRepository monitor=new(source,exigirIdentidadeAcm:true,roleEsperada:roleEsperada);
    readonly ContratosAcmPostgreSqlReadRepository contracts=new(source,exigirIdentidadeAcm:true,roleEsperada:roleEsperada);
    public async Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct)
    {
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        try
        {
            await new NpgsqlCommand("set transaction read only; set local statement_timeout='5s';",connection,tx).ExecuteNonQueryAsync(ct);
            await Guard(connection,tx,ct);
            await using var command=new NpgsqlCommand("select current_setting('cluster_name'),current_setting('port')::int,current_database(),current_user,current_setting('transaction_read_only')='on',clock_timestamp()",connection,tx);
            await using var r=await command.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
            var identity=new DevDatabaseIdentity(r.GetString(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetBoolean(4),r.GetFieldValue<DateTimeOffset>(5));
            // O servidor exige exatamente a identidade que configurou, nao "uma das aceitas".
            identity.Validate(roleEsperada??MonitorPostgreSqlReadRepository.RoleAcmCompartilhada);return identity;
        }
        finally{await tx.RollbackAsync(CancellationToken.None);}
    }
    async Task Guard(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    {
        await using var cmd=MonitorPostgreSqlReadRepository.ComandoIdentidade(roleEsperada,c,tx);
        if(await cmd.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("UNIFIED_DEV_DATABASE_IDENTITY_REJECTED");
    }
    public async Task<object> ReadAsync(string resource,string? selector,CancellationToken ct)
    {
        var identity=await IdentityAsync(ct);
        switch(resource)
        {
            case "contracts":
                var rows=await contracts.ConsultarAsync(string.IsNullOrWhiteSpace(selector)?"3011":selector,ct);
                return new DevReadEnvelope<IReadOnlyList<ContratoAcmLeitura>>(identity,rows.Select(r=>r with {FonteDados=DevReadScope.Classify(r.IntegracaoId,r.NumeroContrato)}).ToArray());
            case "monitor":
                var snapshot=await new MonitorOperacionalConsulta(monitor).ConsultarAsync(new(),ct);
                return new DevReadEnvelope<MonitorSnapshot>(identity,snapshot with {Integracoes=snapshot.Integracoes.Select(r=>r with {
                    UltimoResultado=(r.UltimoResultado??"-")+" | "+DevReadScope.Classify(r.IntegracaoId,r.Documento),
                    Fluxo=DevReadScope.Classify(r.IntegracaoId,r.Documento)=="SIMULACAO_CONFIRMADA"?(r.Fluxo??"")+" / GUARDIAN SIMULADO":r.Fluxo}).ToArray()});
            case "detail":
                return new DevReadEnvelope<MonitorAcmDetalhe>(identity,await monitor.DetalharAcmAsync(ParseId(selector),ct));
            case "protocol":
                var id=ParseId(selector);var all=await monitor.ConsultarAsync(new(),ct);
                var row=all.Integracoes.SingleOrDefault(r=>r.IntegracaoId==id)??throw new KeyNotFoundException();
                var detail=await monitor.DetalharAcmAsync(id,ct);var origin=DevReadScope.Classify(id,detail.Acm.Contrato);
                return new DevReadEnvelope<DevProtocolView>(identity,new(row,detail.Acm,origin=="SIMULACAO_CONFIRMADA",origin));
            case "masterdata":return new DevReadEnvelope<DevMasterdataSnapshot>(identity,await MasterdataAsync(selector,ct));
            case "toledo":return new DevReadEnvelope<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentosToledoSnapshot>(identity,
                await new FugaTES.Persistencia.Consultas.RecebimentosToledo.RecebimentosToledoPostgreSqlReadRepository(source,roleEsperada).ConsultarRecebimentosAsync(selector,ct));
            case "toledo-json":return new DevReadEnvelope<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentoToledoDocumento>(identity,
                await new FugaTES.Persistencia.Consultas.RecebimentosToledo.RecebimentosToledoPostgreSqlReadRepository(source,roleEsperada).ObterJsonRecebidoAsync(ParseId(selector),ct));
            case "toledo-sap-json":return new DevReadEnvelope<FugaTES.Aplicacao.Consultas.RecebimentosToledo.ToledoSapJsonFonte>(identity,
                await new FugaTES.Persistencia.Consultas.RecebimentosToledo.RecebimentosToledoPostgreSqlReadRepository(source,roleEsperada).ObterFonteJsonSapAsync(ParseId(selector),ct));
            default:throw new KeyNotFoundException();
        }
    }
    static Guid ParseId(string? value)=>Guid.TryParse(value,out var id)&&id!=Guid.Empty?id:throw new ArgumentException("INVALID_INTEGRATION_ID");
    async Task<DevMasterdataSnapshot> MasterdataAsync(string? bp,CancellationToken ct)
    {
        if(bp?.Length>40 || bp?.Any(char.IsControl)==true)throw new ArgumentException("INVALID_BP_FILTER");
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        try
        {
            await new NpgsqlCommand("set transaction read only; set local statement_timeout='5s'; set local lock_timeout='1s'",c,tx).ExecuteNonQueryAsync(ct);
            await Guard(c,tx,ct);
            await using var count=new NpgsqlCommand("select count(*) from masterdata.carrier_single_create_journal",c,tx);
            var total=(long)(await count.ExecuteScalarAsync(ct))!;
            await using var cmd=new NpgsqlCommand("select attempt_id,bp,guardian_code,status,prepared_utc,confirmed_utc from masterdata.carrier_single_create_journal where (@bp is null or bp=@bp or guardian_code=@bp) order by prepared_utc desc limit 100",c,tx);
            cmd.Parameters.AddWithValue("bp",NpgsqlTypes.NpgsqlDbType.Text,string.IsNullOrWhiteSpace(bp)?DBNull.Value:bp.Trim());
            await using var reader=await cmd.ExecuteReaderAsync(ct);var list=new List<CarrierCreateHistoryEntry>();
            while(await reader.ReadAsync(ct))list.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetFieldValue<DateTimeOffset>(4),reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5)));
            return new("masterdata",total,list);
        }
        finally{await tx.RollbackAsync(CancellationToken.None);}
    }
}
