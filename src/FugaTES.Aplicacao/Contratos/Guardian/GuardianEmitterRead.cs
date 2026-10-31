namespace FugaTES.Aplicacao.Contratos.Guardian;
public sealed record GuardianEmitterReadResult(bool Confirmado,string Codigo,long Sequencial,int IdAssinatura,int? Http,string Motivo);
public interface IGuardianEmitterRead
{
    Task<GuardianEmitterReadResult> ConsultarAsync(string codigo,CancellationToken cancellationToken);
}
