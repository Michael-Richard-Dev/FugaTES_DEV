namespace FugaTES.Aplicacao.Contratos.Guardian;
public sealed record GuardianMaterialReadResult(bool Confirmado,string Codigo,long Sequencial,int IdAssinatura,int? Http,string Motivo);
public interface IGuardianMaterialRead
{
    Task<GuardianMaterialReadResult> ConsultarAsync(string codigo,CancellationToken cancellationToken);
}

// A code is an identity, not a sequence or an arbitrary URL fragment.
public static class GuardianMaterialCode
{
    public static bool IsValid(string? code)=>code is {Length:>0 and <=20}&&char.IsAsciiLetterOrDigit(code[0])&&
        code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')&&
        (!code.All(char.IsAsciiDigit)||code.Any(c=>c!='0'));
}
