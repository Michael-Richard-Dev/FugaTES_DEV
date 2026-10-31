namespace FugaTES.Aplicacao.Modelos.Recebimento;

public static class GuardianFinalWeighingPolicy
{
    // Validate physical bounds only. Guardian's three authoritative values are never recomputed,
    // rounded, converted, or constrained by an invented per-ticket contract quantity rule.
    public static bool Valid(decimal gross, decimal tare, decimal net) =>
        gross >= 0 && tare >= 0 && net >= 0 && tare <= gross && net <= gross;

    public static void RequireFinal(decimal gross, decimal tare, decimal net)
    {
        if (!Valid(gross, tare, net))
            throw new ArgumentException("GUARDIAN_FINAL_WEIGHTS_INVALID");
    }
}
