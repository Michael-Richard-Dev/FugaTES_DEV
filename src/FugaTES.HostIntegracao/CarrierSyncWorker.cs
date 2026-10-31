using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.HostIntegracao;
public sealed class CarrierSyncWorker(SincronizarTransportadoras motor,TimeProvider clock,ILogger<CarrierSyncWorker> logger):BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => new CarrierPollLoop(async token=>{await motor.ExecutarCicloAsync(token);},clock,code=>logger.LogWarning("{CarrierDiagnostic}",code)).RunAsync(ct);
}