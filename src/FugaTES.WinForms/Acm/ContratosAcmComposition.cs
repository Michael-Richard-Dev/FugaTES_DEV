using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Persistencia.Consultas.ContratosAcm;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

namespace FugaTES.WinForms.Acm;

internal static class ContratosAcmComposition
{
    public static FrmContratosAcm Criar()
    {
        NpgsqlDataSource? source = null;
        IContratosAcmReadRepository repository = new Desconectado();
        if (Runtime.UnifiedProfileGuard.RuntimeLegadoPermitido() is { } runtime)
        {
            source = runtime.CreateReadSource();
            repository = new ContratosAcmPostgreSqlReadRepository(source);
        }
        return new(new ContratosAcmConsulta(repository), source);
    }

    private sealed class Desconectado : IContratosAcmReadRepository
    {
        public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
            => throw new InvalidOperationException("Conexao indisponivel.");
    }
}
