using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
namespace FugaTES.Infraestrutura.ContratosSapToledo;
public sealed class ContratoSapToledoVersaoStoreFake(IMotorIntegracaoRepository motor) : IContratoSapToledoVersaoStore
{
    private readonly object gate = new(); private readonly List<VersaoContratoSapToledo> versions = [];
    public async Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot, string fingerprint, string payload, string correlationId, CancellationToken ct)
    {
        VersaoContratoSapToledo created;
        lock(gate) { var old=versions.SingleOrDefault(x=>x.NumeroContrato==snapshot.NumeroContrato&&x.FingerprintSha256==fingerprint); if(old is not null)return old with{Resultado=ResultadoRegistroVersaoContrato.JaExistente}; var version=versions.Where(x=>x.NumeroContrato==snapshot.NumeroContrato).Select(x=>x.Versao).DefaultIfEmpty().Max()+1; created=new(ResultadoRegistroVersaoContrato.Criada,snapshot.NumeroContrato,version,fingerprint,Guid.NewGuid(),snapshot); versions.Add(created); }
        if (motor is RepositorioMotorIntegracaoFake fake) fake.Semear(new(created.IntegracaoId,"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",snapshot.NumeroContrato,fingerprint,correlationId,EstadoIntegracao.Pendente,TipoDisparoPendente.Automatico,DateTimeOffset.UtcNow,0,null,null,FaseEnvioIntegracao.NaoIniciado,null));
        else await motor.RegistrarCandidatoAsync(new("SAP","TOLEDO","SAP_CONTRATO_TOLEDO",snapshot.NumeroContrato,fingerprint,correlationId),DateTimeOffset.UtcNow,ct);
        return created;
    }
    public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct){lock(gate)return Task.FromResult(versions.SingleOrDefault(x=>x.IntegracaoId==id));}
    public async Task<bool> TemPredecessorNaoTerminalAsync(string numero,long versao,CancellationToken ct){VersaoContratoSapToledo[] previous;lock(gate)previous=versions.Where(x=>x.NumeroContrato==numero&&x.Versao<versao).ToArray();foreach(var item in previous){var integration=await motor.ObterPorIdAsync(item.IntegracaoId,ct);if(integration?.Estado is EstadoIntegracao.Pendente or EstadoIntegracao.EmProcessamento or EstadoIntegracao.AguardandoReprocessamento or EstadoIntegracao.AguardandoAcaoManual or EstadoIntegracao.ResultadoIndeterminado)return true;}return false;}
}
