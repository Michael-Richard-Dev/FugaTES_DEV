using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;
// Not registered in Create(), UI or CarrierPollLoop. Requires the actual lifecycle.Masterdata instance.
public sealed class GuardianSingleCreateManualR31(OrquestradorCompartilhadoMasterdataR3 shared,GuardianSingleCreateUseCaseR31 useCase)
{
    public Task<SingleCreateResultR31> ExecuteAsync(SingleCreateRequestR31 request,CancellationToken ct=default)
        =>shared.ExecutarCriacaoUnitariaAsync(request.AttemptId,token=>useCase.ExecuteAsync(request,token),ct);
}
