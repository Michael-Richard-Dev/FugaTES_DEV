namespace FugaTES.Aplicacao.Modelos.Recebimento;

public sealed record EventoGuardianRecebimento(
    long Id, long Sequencial, int EstadoCodigo, decimal? PesoBruto, decimal? Tara, decimal? PesoLiquido);

public enum StatusRegistroEventoGuardian { Novo, Idempotente, Conflito }
