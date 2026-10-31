#nullable enable
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.ProtocolosGuardian;
using Npgsql;

namespace FugaTES.WinForms.Guardian;

internal static class ProtocolosPreCadastroComposition
{
    public static FrmProtocolosPreCadastro Criar()
    {
        NpgsqlDataSource? source = null;
        IProtocolosPreCadastroReadRepository repository = new Desconectado();
        if (Runtime.UnifiedProfileGuard.RuntimeLegadoPermitido() is { } runtime)
        {
            source = runtime.CreateReadSource();
            repository = new ProtocolosPreCadastroPostgreSqlReadRepository(new MonitorPostgreSqlReadRepository(source));
        }
        return new(new ProtocolosPreCadastroConsulta(repository), source, AbrirContrato);
    }

    /// <summary>
    /// Navegacao ate o contrato SAP correspondente. A tela de contratos consulta por
    /// centro e nao por numero, de modo que ela e aberta e o contrato fica anotado no
    /// titulo; nenhum dado do contrato e inferido aqui.
    /// </summary>
    private static void AbrirContrato(ProtocoloPreCadastroLinha linha, IWin32Window dono)
    {
        using var tela = Acm.ContratosAcmComposition.Criar();
        tela.Text += " — contrato " + ProtocoloPreCadastro.Texto(linha.Contrato);
        tela.ShowDialog(dono);
    }

    private sealed class Desconectado : IProtocolosPreCadastroReadRepository
    {
        public Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct)
            => throw new InvalidOperationException("Conexao indisponivel.");
    }
}
