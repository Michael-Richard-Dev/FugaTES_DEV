namespace FugaTES.Aplicacao.Transportadoras;

public enum DecisaoCarrierManual { CREATE_CANDIDATE, UPDATE_CANDIDATE, NO_ACTION, INDETERMINATE, BLOCKED }
public sealed record ResultadoCarrierManual(string BusinessPartner, DecisaoCarrierManual Decisao, string Motivo)
{
    public static ResultadoCarrierManual Bloqueado(string bp,string motivo)=>new(bp,DecisaoCarrierManual.BLOCKED,motivo);
}
public sealed record CarrierManualRole(string Role,DateTimeOffset? ValidFrom,DateTimeOffset? ValidTo,bool InstantesComprovados=false);
public sealed record CarrierManualSap(string BusinessPartner,IReadOnlyDictionary<string,string?> Campos,IReadOnlyList<CarrierManualRole> Roles,bool Bloqueado=false,bool RolesCompletas=true);
public sealed record CarrierManualIdentity(string Codigo,string IdAssinatura,bool Comprovada,string Evidencia);
public sealed record CarrierManualGuardian(CarrierLookupState State,string? Codigo,IReadOnlyDictionary<string,string?> Campos,bool Ausencia404Comprovada=false);
public sealed record CarrierManualField(string Sap,string Guardian,bool CorrespondenciaComprovada);
public sealed record CarrierManualComparison(IReadOnlyList<CarrierManualField> Campos,bool EquivalenciaCompletaComprovada,string Evidencia);
public interface ISapCarrierManualReadOnly { Task<CarrierManualSap> ConsultarBpAsync(string bp,CancellationToken ct); }
public interface IGuardianCarrierManualReadOnly { Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity identity,CancellationToken ct); }
public interface IConsultaTransportadoraManualReadOnly { Task<ResultadoCarrierManual> ExecutarAsync(string bp,CancellationToken ct); }
public sealed class ConsultarTransportadoraManualReadOnlyCasoDeUso(
    ISapCarrierManualReadOnly sap,IGuardianCarrierManualReadOnly guardian,
    Func<string,bool> autorizado,Func<string,CarrierManualIdentity?> identidade,
    CarrierManualComparison comparacao,TimeProvider tempo):IConsultaTransportadoraManualReadOnly
{
    public async Task<ResultadoCarrierManual> ExecutarAsync(string bp,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try{CarrierPersistencePolicy.ValidateBp(bp);}catch{return ResultadoCarrierManual.Bloqueado(bp,"MANUAL_BP_INVALID");}
        if(!autorizado(bp))return ResultadoCarrierManual.Bloqueado(bp,"MANUAL_HML_NOT_AUTHORIZED");
        CarrierManualSap data;
        try{data=await sap.ConsultarBpAsync(bp,ct).ConfigureAwait(false);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{return new(bp,DecisaoCarrierManual.INDETERMINATE,"SAP_READ_FAILED");}
        if(data.BusinessPartner!=bp||data.Bloqueado)return ResultadoCarrierManual.Bloqueado(bp,"SAP_IDENTITY_OR_BLOCK_INVALID");
        var eligibility=Elegibilidade(data,tempo.GetUtcNow());
        if(eligibility!=1)return new(bp,eligibility==0?DecisaoCarrierManual.BLOCKED:DecisaoCarrierManual.INDETERMINATE,
            eligibility==0?"CRM010_NOT_ACTIVE":"CRM010_TEMPORAL_INDETERMINATE");
        var identity=identidade(bp);
        if(identity is not {Comprovada:true}||string.IsNullOrWhiteSpace(identity.Codigo)||string.IsNullOrWhiteSpace(identity.IdAssinatura)||string.IsNullOrWhiteSpace(identity.Evidencia))
            return new(bp,DecisaoCarrierManual.INDETERMINATE,"GUARDIAN_BP_CODE_BINDING_NOT_PROVEN");
        // Recheck expiring authorization before contacting the second system.
        if(!autorizado(bp))return ResultadoCarrierManual.Bloqueado(bp,"MANUAL_AUTHORIZATION_EXPIRED");
        CarrierManualGuardian record;
        try{record=await guardian.ConsultarCodigoAsync(identity,ct).ConfigureAwait(false);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{return new(bp,DecisaoCarrierManual.INDETERMINATE,"GUARDIAN_READ_FAILED");}
        if(record.State==CarrierLookupState.Absent)return new(bp,record.Ausencia404Comprovada?DecisaoCarrierManual.CREATE_CANDIDATE:DecisaoCarrierManual.INDETERMINATE,
            record.Ausencia404Comprovada?"SCOPED_404_CREATE_CANDIDATE":"GUARDIAN_ABSENCE_NOT_PROVEN");
        if(record.State!=CarrierLookupState.Unique||record.Codigo!=identity.Codigo)
            return new(bp,DecisaoCarrierManual.INDETERMINATE,"GUARDIAN_IDENTITY_NOT_UNIQUE");
        if(string.IsNullOrWhiteSpace(comparacao.Evidencia)||comparacao.Campos.Count==0)
            return new(bp,DecisaoCarrierManual.INDETERMINATE,"COMPARISON_NOT_PROVEN");
        bool incomplete=false,different=false;
        foreach(var rule in comparacao.Campos)
        {
            if(!rule.CorrespondenciaComprovada||!data.Campos.TryGetValue(rule.Sap,out var a)||!record.Campos.TryGetValue(rule.Guardian,out var b)||a is null||b is null){incomplete=true;continue;}
            // Exact ordinal comparison only: no implicit trim, casing, tax/address/UF normalization.
            different|=!string.Equals(a,b,StringComparison.Ordinal);
        }
        if(different)return new(bp,DecisaoCarrierManual.UPDATE_CANDIDATE,"PROVEN_FIELD_DIFFERENCE");
        return new(bp,!incomplete&&comparacao.EquivalenciaCompletaComprovada?DecisaoCarrierManual.NO_ACTION:DecisaoCarrierManual.INDETERMINATE,
            !incomplete&&comparacao.EquivalenciaCompletaComprovada?"COMPLETE_EQUIVALENCE_PROVEN":"PARTIAL_COMPARISON_ONLY");
    }
    static int Elegibilidade(CarrierManualSap data,DateTimeOffset reference)
    {
        bool uncertain=!data.RolesCompletas;
        foreach(var role in data.Roles.Where(r=>r.Role=="CRM010"))
        {
            if(role.ValidFrom is not {} from||role.ValidTo is not {} to||from>to){uncertain=true;continue;}
            if(role.InstantesComprovados){if(from<=reference&&reference<=to)return 1;continue;}
            // Compare all supported interpretations: exact UTC instants and civil dates
            // represented in UTC or the explicit wire offset, across offsets [-14h,+14h].
            // This is an uncertainty envelope, not an adopted SAP timezone/end-of-day rule.
            var start=Bounds(from,false);var end=Bounds(to,true);
            if(reference>=start.Latest&&reference<end.Earliest)return 1;
            if(reference>=start.Earliest&&reference<=end.Latest)uncertain=true;
        }
        return uncertain?-1:0;
    }
    static (DateTimeOffset Earliest,DateTimeOffset Latest) Bounds(DateTimeOffset value,bool end)
    {
        var values=new List<DateTimeOffset>{value};
        foreach(var date in new[]{value.UtcDateTime.Date,value.Date})
        {
            var ticks=date.Ticks+(end?TimeSpan.TicksPerDay:0);
            foreach(var shift in new[]{-14,14})
                values.Add(new DateTimeOffset(Math.Clamp(ticks+TimeSpan.FromHours(shift).Ticks,DateTimeOffset.MinValue.Ticks,DateTimeOffset.MaxValue.Ticks),TimeSpan.Zero));
        }
        return(values.Min(),values.Max());
    }
}
