namespace FugaTES.Aplicacao.Transportadoras.SnapshotsR24;

// Proposed future storage contract, not an implemented PostgreSQL adapter.
public sealed record ResultadoPrecisaoTemporalR25D(long CanonicalUtcTicks,DateTimeOffset? OperationalMicrosecondInstant,string Code)
{
    public bool PrecisionLossBlocked=>OperationalMicrosecondInstant is null;
    public bool SqlRoundTripProven=>false;
}
public static class SnapshotTemporalPrecisionR25D
{
    public static ResultadoPrecisaoTemporalR25D Propose(DateTimeOffset instant)=>
        new(instant.UtcTicks,instant.UtcTicks%10==0?instant.ToUniversalTime():null,
            instant.UtcTicks%10==0?"EXACT_AUXILIARY_REPRESENTATION":"PRECISION_LOSS_BLOCKED");
    public static DateTimeOffset RestoreCanonicalTicks(long ticks)=>new(ticks,TimeSpan.Zero);
}
