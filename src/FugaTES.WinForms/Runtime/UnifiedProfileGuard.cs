using System.Text.Json;

namespace FugaTES.WinForms.Runtime;

/// <summary>
/// Perfil DEV110 unificado: quando ele esta ativo, a fonte canonica de leitura e o PostgreSQL DEV
/// 5433, acessado pela API privada somente leitura. As composicoes legadas de leitura
/// (Contratos ACM, Monitor e Protocolos) abrem um datasource proprio a partir de
/// FugaTES.runtime.json, cujo destino e o cluster anterior na porta 5432.
///
/// Na pratica o arranque unificado nunca chama DesktopRuntime.Initialize(), portanto
/// DesktopRuntime.Current e nulo e nenhuma conexao 5432 chega a ser aberta. Esta guarda
/// transforma essa ausencia em recusa explicita: se por qualquer caminho futuro o runtime
/// legado estiver inicializado enquanto o perfil unificado vale, a composicao legada
/// permanece desconectada em vez de ler de uma fonte divergente.
///
/// A configuracao do sistema legado NAO e alterada: FugaTES.runtime.json continua intacto e o
/// caminho legado (--monitor-only) continua funcionando normalmente quando o perfil unificado
/// nao esta presente.
/// </summary>
public static class UnifiedProfileGuard
{
    public const string ArquivoPerfil = "FugaTES.unified.json";

    private sealed record Perfil
    {
        public bool PhysicalReadbackEnabled { get; init; }
        public bool UnifiedDatabaseReadback { get; init; }
    }

    /// <summary>
    /// Verdadeiro quando o diretorio da aplicacao declara o perfil unificado com readback fisico.
    /// Qualquer falha de leitura ou parse devolve falso: a guarda nunca bloqueia o caminho legado
    /// por causa de um arquivo ilegivel.
    /// </summary>
    public static bool Ativo(string? baseDirectory = null)
    {
        try
        {
            var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, ArquivoPerfil);
            if (!File.Exists(path)) return false;
            var perfil = JsonSerializer.Deserialize<Perfil>(File.ReadAllText(path));
            return perfil is { PhysicalReadbackEnabled: true } || perfil is { UnifiedDatabaseReadback: true };
        }
        catch { return false; }
    }

    /// <summary>
    /// Devolve o runtime legado somente quando o perfil unificado NAO esta ativo. Com o perfil
    /// unificado ativo devolve nulo, e a composicao legada cai no seu proprio repositorio
    /// desconectado, sem criar datasource para a porta 5432.
    /// </summary>
    public static DesktopRuntime? RuntimeLegadoPermitido(string? baseDirectory = null)
        => Ativo(baseDirectory) ? null : DesktopRuntime.Current;
}
