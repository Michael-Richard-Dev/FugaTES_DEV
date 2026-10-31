namespace FugaTES.Aplicacao.Transportadoras.SnapshotsR24;

public sealed record ResultadoTemporalCrm010R25T(AvaliacaoCrm010R24 Status,DateOnly? ReferenceDate,DateOnly? StartDate,DateOnly? EffectiveEndDate,string Code);

// Functional contract explicitly approved by the responsible FugaTES user (R25T).
// ReferenceAt is supplied by the scan; this policy never consults a clock.
public sealed class Crm010BusinessDatePolicyR25T(Func<string,TimeZoneInfo?>? zoneResolver=null)
{
    public const string PolicyVersion="R25T_BUSINESS_DATE_INCLUSIVE_MIDNIGHT_EXCLUSIVE_V1";
    public const string BusinessTimezone="America/Sao_Paulo";
    public ResultadoTemporalCrm010R25T Evaluate(DateTimeOffset? validFrom,DateTimeOffset? validTo,DateTimeOffset referenceAt)
    {
        static ResultadoTemporalCrm010R25T Unknown(string code)=>new(AvaliacaoCrm010R24.INDETERMINATE,null,null,null,code);
        if(validFrom is not {} from||validTo is not {} to)return Unknown("CRM010_DATE_MISSING");
        if(from>to)return Unknown("CRM010_RANGE_INCOHERENT");
        try
        {
            var zone=zoneResolver is null?TimeZoneInfo.FindSystemTimeZoneById(BusinessTimezone):zoneResolver(BusinessTimezone);
            if(zone is null||zone.Id!=BusinessTimezone)return Unknown("CRM010_TIMEZONE_UNAVAILABLE");
            var localFrom=TimeZoneInfo.ConvertTime(from,zone);var localTo=TimeZoneInfo.ConvertTime(to,zone);
            var start=DateOnly.FromDateTime(localFrom.DateTime);var end=DateOnly.FromDateTime(localTo.DateTime);
            if(localTo.TimeOfDay==TimeSpan.Zero)end=end.AddDays(-1);
            if(start>end)return Unknown("CRM010_RANGE_INCOHERENT");
            var reference=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(referenceAt,zone).DateTime);
            return new(start<=reference&&reference<=end?AvaliacaoCrm010R24.EVALUATED_VALID:AvaliacaoCrm010R24.EVALUATED_INVALID,reference,start,end,"CRM010_BUSINESS_DATE_EVALUATED");
        }
        catch(TimeZoneNotFoundException){return Unknown("CRM010_TIMEZONE_UNAVAILABLE");}
        catch(InvalidTimeZoneException){return Unknown("CRM010_TIMEZONE_UNAVAILABLE");}
        catch(ArgumentException){return Unknown("CRM010_TEMPORAL_CONVERSION_INVALID");}
    }
}
