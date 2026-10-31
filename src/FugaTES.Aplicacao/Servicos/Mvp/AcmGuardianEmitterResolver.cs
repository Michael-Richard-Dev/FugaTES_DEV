using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
namespace FugaTES.Aplicacao.Servicos.Mvp;

public interface IAcmR57DynamicEmitterPreparation
{Task ValidarEmissorAsync(VersaoContratoSapToledo version,CancellationToken ct);}

/// <summary>Prova GET somente no scope da execução; nunca modifica a lista de aprovações.</summary>
public sealed class AcmGuardianEmitterResolver(AcmR57NormalOptions options,IGuardianEmitterRead lookup,TimeProvider clock)
{
    private Proof? proof;
    private sealed record Proof(Guid IntegrationId,string Fingerprint,string SapPartner,string? Item,string Centro,string Codigo,long Sequencial,DateTimeOffset At);
    public static bool UsesDynamic(AcmR57NormalOptions o,VersaoContratoSapToledo v)
    {
        if(!o.DynamicEmitterValidationEnabled||o.AcmInboundWindow.Enabled)return false;
        if(o.DynamicEmitterSignatureId<=0||o.DynamicEmitterValidationFromUtc is null||v.RecebidoEmUtc is null)
            throw new AcmR57BlockedException("R171_EMITTER_ADMISSION_IDENTITY_REQUIRED");
        return v.RecebidoEmUtc>=o.DynamicEmitterValidationFromUtc;
    }
    public async Task ValidarAsync(VersaoContratoSapToledo version,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();proof=null;
        if(!UsesDynamic(options,version))throw new AcmR57BlockedException("R171_EMITTER_DYNAMIC_SCOPE_REQUIRED");
        _=AcmR57NormalPreparation.ResolverMaterialEscopo(options,version);
        var sap=version.Snapshot.IdParceiro;
        if(string.IsNullOrEmpty(sap)||sap.Length>20||!sap.All(char.IsAsciiDigit)||sap.All(c=>c=='0'))throw new AcmR57BlockedException("R171_EMITTER_CODE_INVALID");
        // Candidate representation only. Identity is accepted only after the exact GET response.
        var candidate=sap.TrimStart('0');
        GuardianEmitterReadResult result;
        try{result=await lookup.ConsultarAsync(candidate,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{throw new AcmR57BlockedException("R171_EMITTER_LOOKUP_UNVERIFIED");}
        if(!result.Confirmado)
        {
            var allowed=new[]{"R171_EMITTER_NOT_FOUND","R171_EMITTER_INACTIVE","R171_EMITTER_LOOKUP_UNVERIFIED","R171_EMITTER_AUTHENTICATION_FAILED","R171_EMITTER_AUTHORIZATION_FAILED","R171_EMITTER_IDENTITY_MISMATCH","R171_EMITTER_SCOPE_MISMATCH","R171_EMITTER_RESPONSE_INVALID","R171_EMITTER_PROFILE_INVALID","R171_EMITTER_CODE_INVALID"};
            throw new AcmR57BlockedException(allowed.Contains(result.Motivo,StringComparer.Ordinal)?result.Motivo:"R171_EMITTER_LOOKUP_UNVERIFIED");
        }
        if(result.IdAssinatura!=options.DynamicEmitterSignatureId)throw new AcmR57BlockedException("R171_EMITTER_SCOPE_MISMATCH");
        if(result.Http!=200||result.Codigo!=candidate||result.Sequencial<=0)throw new AcmR57BlockedException("R171_EMITTER_IDENTITY_MISMATCH");
        proof=new(version.IntegracaoId,version.FingerprintSha256,sap,version.Snapshot.Item,version.Snapshot.Centro!,result.Codigo,result.Sequencial,clock.GetUtcNow());
    }
    public string Exigir(VersaoContratoSapToledo version)
    {
        if(proof is null||proof.IntegrationId!=version.IntegracaoId||proof.Fingerprint!=version.FingerprintSha256||proof.SapPartner!=version.Snapshot.IdParceiro||proof.Item!=version.Snapshot.Item||proof.Centro!=version.Snapshot.Centro)
            throw new AcmR57BlockedException("R171_EMITTER_VALIDATION_REQUIRED");
        var age=clock.GetUtcNow()-proof.At;
        if(age<TimeSpan.Zero||age>TimeSpan.FromMinutes(2))throw new AcmR57BlockedException("R171_EMITTER_PROOF_EXPIRED");
        return proof.Codigo;
    }
}

public static class AcmR57DynamicAdmission
{
    public static async Task<string?> BloqueioAsync(IAcmR57RuntimeGate gate,IAcmR57Preparacao preparation,VersaoContratoSapToledo version,CancellationToken ct)
    {
        var block=gate.Bloqueio(version);
        if(block is not("R171_EMITTER_VALIDATION_REQUIRED" or "R171_EMITTER_PROOF_EXPIRED"))return block;
        if(preparation is not IAcmR57DynamicEmitterPreparation dynamic)return block;
        try{await dynamic.ValidarEmissorAsync(version,ct);return gate.Bloqueio(version);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(AcmR57BlockedException e){return e.Code;}
        catch{return "R171_EMITTER_LOOKUP_UNVERIFIED";}
    }
}
