using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed class AcmR104OperationalProfile
{
    public bool ExecutionAuthorized { get; init; }
    public string? AuthorizationReference { get; init; }
    public string? GuardianEnvironment { get; init; }
    public bool GuardianHmlIdentityVerified { get; init; }
    public string? GuardianIdentityEvidence { get; init; }
    public bool PostgreSqlIdentityAttested { get; init; }
    public string? PostgreSqlIdentityEvidence { get; init; }
    public bool JournalPhysicalReadinessAttested { get; init; }
    public string? JournalEvidence { get; init; }
    public AcmR57Vinculos? Bindings { get; init; }
}

public static class AcmR104Scope
{
    public static readonly Guid IntegrationId=new("9831f489-7c18-4377-b169-9040e3af2755");
    public const string Contract="0000001106";
    public const long H4Version=1;
    public const string Fingerprint="35CCC6966F1B5A72F6C70EDDFF9180C7FD20B299650057F87568445CC6AE512C";
    public static bool Matches(VersaoContratoSapToledo v)=>v.IntegracaoId==IntegrationId && v.NumeroContrato==Contract &&
        v.Versao==H4Version && v.FingerprintSha256==Fingerprint;
    public static string? ApprovalBlock(AcmR104OperationalProfile p)
    {
        if(!p.ExecutionAuthorized || string.IsNullOrWhiteSpace(p.AuthorizationReference))return "R104_EXECUTION_DISABLED";
        if(p.GuardianEnvironment!="HML" || !p.GuardianHmlIdentityVerified || string.IsNullOrWhiteSpace(p.GuardianIdentityEvidence))return "R104_GUARDIAN_HML_IDENTITY_UNVERIFIED";
        if(!p.PostgreSqlIdentityAttested || string.IsNullOrWhiteSpace(p.PostgreSqlIdentityEvidence))return "R104_POSTGRESQL_IDENTITY_UNATTESTED";
        if(!p.JournalPhysicalReadinessAttested || string.IsNullOrWhiteSpace(p.JournalEvidence))return "R104_PHYSICAL_JOURNAL_UNATTESTED";
        if(p.Bindings is null)return "R104_BUSINESS_BINDINGS_REQUIRED";
        return null;
    }
}

public sealed class AcmR104RuntimeGate(AcmR104OperationalProfile profile):IAcmR57RuntimeGate
{
    public string? Bloqueio(VersaoContratoSapToledo version)=>AcmR104Scope.ApprovalBlock(profile) ??
        (AcmR104Scope.Matches(version)?null:"R104_SCOPE_MISMATCH");
}
public sealed class AcmR104Preparation(AcmR104OperationalProfile profile):IAcmR57Preparacao
{
    public AcmR57Vinculos Obter(VersaoContratoSapToledo v)
    {
        var block=new AcmR104RuntimeGate(profile).Bloqueio(v);
        if(block is not null)throw new AcmR57BlockedException(block);
        return profile.Bindings!;
    }
}
public interface IAcmR104TargetedDispatch
{
    Task<CicloMotorIntegracaoResultado> ExecutarAsync(Guid id,CancellationToken ct);
}
public sealed record AcmR104ExecutionResult(bool Invoked,string Code,CicloMotorIntegracaoResultado? MotorResult=null);

public sealed class AcmR104UnitaryInvoker(AcmR104OperationalProfile profile,IContratoSapToledoVersaoStore versions,
    IMotorIntegracaoRepository repository,IResultadoFluxoMvpStore journal,IAcmR104TargetedDispatch dispatch)
{
    public async Task<AcmR104ExecutionResult> ExecutarAsync(Guid id,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var block=AcmR104Scope.ApprovalBlock(profile);
        if(block is not null)return new(false,block);
        if(id!=AcmR104Scope.IntegrationId)return new(false,"R104_SCOPE_MISMATCH");
        var version=await versions.ObterPorIntegracaoIdAsync(id,ct);
        if(version is null || !AcmR104Scope.Matches(version))return new(false,"R104_SOURCE_SCOPE_MISMATCH");
        var integration=await repository.ObterPorIdAsync(id,ct);
        if(integration is null || integration.Id!=id || integration.Origem!="SAP" || integration.Destino!="TOLEDO" ||
            integration.Operacao!="SAP_CONTRATO_TOLEDO" || integration.ChaveNegocio!=version.NumeroContrato ||
            integration.FingerprintSha256!=version.FingerprintSha256)return new(false,"R104_INTEGRATION_IDENTITY_MISMATCH");
        if(integration.Estado!=EstadoIntegracao.Pendente)return new(false,"R104_INTEGRATION_NOT_PENDING");
        await journal.ExigirDisponibilidadeAsync(ct);
        // Read-only journal lookup: no fabricated claim is used to reserve or dispatch anything.
        var lookup=new ClaimIntegracaoMotor(id,integration.TentativaAtualId??Guid.Empty,integration.CorrelationId,
            integration.Origem,integration.Destino,integration.Operacao,integration.ChaveNegocio,integration.FingerprintSha256,
            integration.FencingToken,integration.WorkerProprietario??string.Empty,integration.LeaseExpiraEm??default);
        var previous=await journal.ObterAsync(lookup,ct);
        if(previous is null && (integration.FencingToken!=0 || integration.FaseEnvio!=FaseEnvioIntegracao.NaoIniciado))
            return new(false,"R104_JOURNAL_HISTORY_UNPROVEN");
        if(previous is not null && !(previous.Status==FugaTES.Aplicacao.Modelos.Mvp.StatusFluxoMvp.AguardandoDependenciaExterna && previous.Etapa=="R57_PRE_SEND_BLOCKED") &&
            !(previous.Status==FugaTES.Aplicacao.Modelos.Mvp.StatusFluxoMvp.EmProcessamento && previous.Etapa=="R57_SOAP_CONFIRMED"))
            return new(false,"R104_JOURNAL_RECONCILIATION_REQUIRED");
        // The existing motor claims this one ID with lease/fencing. The R57 pipeline revalidates
        // canonical H4, business mappings, journal CAS and protocol uniqueness before network effects.
        var result=await dispatch.ExecutarAsync(id,ct);
        var final=await journal.ObterAsync(lookup,ct);
        var confirmed=final is {Status:FugaTES.Aplicacao.Modelos.Mvp.StatusFluxoMvp.AguardandoCallback,Etapa:"R57_CONFIRMED",Erro:null} &&
            long.TryParse(final.ProtocoloGuardian,out var protocol) && protocol>0;
        return new(true,confirmed?"R104_TARGET_CONFIRMED":"R104_TARGET_UNCONFIRMED",result);
    }
}
