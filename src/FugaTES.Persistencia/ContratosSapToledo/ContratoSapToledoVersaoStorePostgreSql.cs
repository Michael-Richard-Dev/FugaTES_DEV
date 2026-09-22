using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;
using NpgsqlTypes;
namespace FugaTES.Persistencia.ContratosSapToledo;
public sealed class ContratoSapToledoVersaoStorePostgreSql(NpgsqlDataSource dataSource) : IContratoSapToledoVersaoStore
{
    public async Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,string fingerprint,string payload,string correlationId,CancellationToken ct)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct); await using var tx=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try {
            await using(var ensure=new NpgsqlCommand("insert into contrato_sap_toledo_identidade(numero_contrato) values(@numero) on conflict(numero_contrato) do nothing;",connection,tx)){ensure.Parameters.AddWithValue("numero",snapshot.NumeroContrato);await ensure.ExecuteNonQueryAsync(ct);}
            await using(var locked=new NpgsqlCommand("select ultima_versao from contrato_sap_toledo_identidade where numero_contrato=@numero for update;",connection,tx)){locked.Parameters.AddWithValue("numero",snapshot.NumeroContrato);var last=Convert.ToInt64(await locked.ExecuteScalarAsync(ct));
                await using var find=new NpgsqlCommand("select versao,integracao_id,payload_contrato::text from contrato_sap_toledo_versao where numero_contrato=@numero and fingerprint_sha256=@fingerprint;",connection,tx);find.Parameters.AddWithValue("numero",snapshot.NumeroContrato);find.Parameters.AddWithValue("fingerprint",fingerprint);await using var reader=await find.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){var version=reader.GetInt64(0);var id=reader.GetGuid(1);var json=reader.GetString(2);await reader.DisposeAsync();await tx.CommitAsync(ct);return new(ResultadoRegistroVersaoContrato.JaExistente,snapshot.NumeroContrato,version,fingerprint,id,JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(json)!);}await reader.DisposeAsync();
                var idNew=Guid.NewGuid();var versionNew=last+1;var candidate=new CandidatoIntegracao("SAP","TOLEDO","SAP_CONTRATO_TOLEDO",snapshot.NumeroContrato,fingerprint,correlationId);var registered=await RepositorioMotorIntegracaoPostgreSql.RegistrarCandidatoNaTransacaoAsync(connection,tx,candidate,idNew,ct);if(registered!=ResultadoRegistroIntegracao.Criada)throw new InvalidOperationException("H4 integration fingerprint already exists without matching version.");
                await using(var insert=new NpgsqlCommand("insert into contrato_sap_toledo_versao(numero_contrato,versao,fingerprint_sha256,payload_contrato,integracao_id) values(@numero,@versao,@fingerprint,@payload,@id);",connection,tx)){insert.Parameters.AddWithValue("numero",snapshot.NumeroContrato);insert.Parameters.AddWithValue("versao",versionNew);insert.Parameters.AddWithValue("fingerprint",fingerprint);insert.Parameters.AddWithValue("payload",NpgsqlDbType.Jsonb,payload);insert.Parameters.AddWithValue("id",idNew);await insert.ExecuteNonQueryAsync(ct);}
                await using(var update=new NpgsqlCommand("update contrato_sap_toledo_identidade set ultima_versao=@versao where numero_contrato=@numero;",connection,tx)){update.Parameters.AddWithValue("versao",versionNew);update.Parameters.AddWithValue("numero",snapshot.NumeroContrato);await update.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);return new(ResultadoRegistroVersaoContrato.Criada,snapshot.NumeroContrato,versionNew,fingerprint,idNew,snapshot);
            }
        } catch { await tx.RollbackAsync(ct); throw; }
    }
    public async Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct){const string sql="select numero_contrato,versao,fingerprint_sha256,payload_contrato::text from contrato_sap_toledo_versao where integracao_id=@id;";await using var cmd=dataSource.CreateCommand(sql);cmd.Parameters.AddWithValue("id",id);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(ResultadoRegistroVersaoContrato.JaExistente,r.GetString(0),r.GetInt64(1),r.GetString(2),id,JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(r.GetString(3))!):null;}
    public async Task<bool> TemPredecessorNaoTerminalAsync(string numero,long versao,CancellationToken ct){const string sql="select exists(select 1 from contrato_sap_toledo_versao v join integracao i on i.id=v.integracao_id where v.numero_contrato=@numero and v.versao<@versao and i.estado in ('PENDENTE','EM_PROCESSAMENTO','AGUARDANDO_REPROCESSAMENTO','AGUARDANDO_ACAO_MANUAL','RESULTADO_INDETERMINADO'));";await using var cmd=dataSource.CreateCommand(sql);cmd.Parameters.AddWithValue("numero",numero);cmd.Parameters.AddWithValue("versao",versao);return (bool)(await cmd.ExecuteScalarAsync(ct)??false);}
}
