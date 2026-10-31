using System.Net.Sockets;
using FugaTES.WinForms.Runtime;
using Npgsql;

namespace FugaTES.WinForms.Sd;

/// <summary>
/// Conexao de ESCRITA do fluxo SD com o PostgreSQL DEV, cluster 5433.
///
/// Existe separada do runtime legado por um motivo concreto, descoberto em 31/10/2026: o
/// FugaTES.runtime.json tuneliza para a porta 5433 do host remoto? Nao - para a 5432, o cluster
/// ANTERIOR. E o perfil unificado le o 5433 por uma API privada somente leitura. Nenhum dos dois
/// escreve na tabela de protocolos, que vive no 5433. Reaproveitar qualquer um deles gravaria no
/// lugar errado ou nao gravaria.
///
/// Reusa DesktopConfiguration e DesktopTunnel - a diferenca esta no arquivo de perfil e na
/// transacao, que aqui nao e somente leitura.
/// </summary>
public static class ConexaoSdPostgreSql
{
    public const string ArquivoPerfil = "FugaTES.sd.json";

    /// <summary>Porta remota obrigatoria. O 5432 e o cluster antigo e nao tem esta tabela.</summary>
    public const int PortaRemotaExigida = 5433;

    /// <summary>
    /// Papel proprio do fluxo SD. Nao reusa fugates_dev_app: ele e NOLOGIN no cluster 5433
    /// (conferido em 31/10/2026), e reusar o papel do motor ACM misturaria responsabilidades -
    /// revogar um revogaria o outro.
    /// </summary>
    public const string UsuarioExigido = "fugates_sd_app";

    public sealed record Resultado(NpgsqlDataSource? Fonte, IDisposable? Posse, string? Motivo);

    /// <summary>
    /// Abre a fonte de escrita. Devolve o motivo em vez de lancar: a tela precisa dizer ao
    /// operador o que falta, e nao apenas "indisponivel".
    /// </summary>
    public static async Task<Resultado> AbrirAsync(CancellationToken ct)
    {
        DesktopTunnel? tunel = null;
        try
        {
            var caminho = Path.Combine(AppContext.BaseDirectory, ArquivoPerfil);
            if (!File.Exists(caminho))
                return new(null, null, "SD_PERFIL_BANCO_AUSENTE (" + ArquivoPerfil + ")");

            var config = DesktopConfiguration.Load(caminho);
            config.Validar();
            // Travas proprias do SD, alem das que DesktopConfiguration ja faz.
            if (config.Ssh.RemoteDatabasePort != PortaRemotaExigida)
                return new(null, null, "SD_PORTA_REMOTA_INVALIDA (esperado " + PortaRemotaExigida + ")");
            if (config.Database.Username != UsuarioExigido)
                return new(null, null, "SD_USUARIO_INVALIDO (esperado " + UsuarioExigido + ")");

            var senha = WindowsCredentialProvider.ReadPassword(
                config.Database.CredentialTarget, config.Database.Username);

            // Quem ja esta na porta pode nao ser o tunel certo: um tunel orfao de uma execucao
            // anterior, levantado com outra chave ou para outro destino, responde igual e sequestra
            // a conexao em silencio. Entao a porta ocupada e apenas uma HIPOTESE, confirmada pela
            // conferencia de identidade logo abaixo; se ela falhar, subimos o nosso em outra porta.
            var porta = config.Database.Port;
            var emprestada = await PortaAtendeAsync(porta, ct).ConfigureAwait(false);
            if (!emprestada)
            {
                tunel = DesktopTunnel.Start(config);
                if (await EsperarPortaAsync(tunel, porta, config.ReadinessSeconds, ct).ConfigureAwait(false) is { } falha)
                { tunel.Dispose(); return new(null, null, falha); }
            }

            var tentativa = await ConectarAsync(config, porta, senha, ct).ConfigureAwait(false);
            if (tentativa.Motivo is not null && emprestada)
            {
                // A porta era de outro. Sobe o nosso tunel numa porta livre e tenta de novo, em vez
                // de desistir por causa de um processo que nem e nosso.
                porta = PortaLivre();
                tunel = DesktopTunnel.Start(config with { Database = config.Database with { Port = porta } });
                if (await EsperarPortaAsync(tunel, porta, config.ReadinessSeconds, ct).ConfigureAwait(false) is { } falha)
                { tunel.Dispose(); return new(null, null, falha); }
                tentativa = await ConectarAsync(config, porta, senha, ct).ConfigureAwait(false);
            }

            if (tentativa.Motivo is not null) { tunel?.Dispose(); return new(null, null, tentativa.Motivo); }
            return new(tentativa.Fonte, new Posse(tentativa.Fonte!, tunel), null);
        }
        catch (DesktopStartupException erro) { tunel?.Dispose(); return new(null, null, erro.Code); }
        catch (OperationCanceledException) { tunel?.Dispose(); throw; }
        // O SqlState diz exatamente o que houve e nao e segredo: 28P01 e senha invalida, 3D000 e
        // banco inexistente, 42501 e falta de privilegio. Sem isso tudo vira "indisponivel".
        catch (PostgresException erro) { tunel?.Dispose(); return new(null, null, "SD_BANCO_SQLSTATE_" + erro.SqlState); }
        catch (Exception erro) { tunel?.Dispose(); return new(null, null, "SD_BANCO_" + erro.GetType().Name); }
    }

    /// <summary>Espera a porta atender. Devolve o motivo da falha, ou nulo quando atendeu.</summary>
    private static async Task<string?> EsperarPortaAsync(DesktopTunnel tunel, int porta, int segundos, CancellationToken ct)
    {
        var limite = DateTimeOffset.UtcNow.AddSeconds(segundos);
        while (!await PortaAtendeAsync(porta, ct).ConfigureAwait(false))
        {
            if (tunel.HasExited) return "SD_TUNEL_ENCERROU";
            if (DateTimeOffset.UtcNow >= limite) return "SD_TUNEL_SEM_RESPOSTA";
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Porta local livre, escolhida pelo proprio sistema.</summary>
    private static int PortaLivre()
    {
        var ouvinte = new TcpListener(System.Net.IPAddress.Loopback, 0);
        ouvinte.Start();
        try { return ((System.Net.IPEndPoint)ouvinte.LocalEndpoint).Port; }
        finally { ouvinte.Stop(); }
    }

    private static async Task<Resultado> ConectarAsync(
        DesktopConfiguration config, int porta, string senha, CancellationToken ct)
    {
        NpgsqlDataSource? fonte = null;
        try
        {
            fonte = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
            {
                Host = config.Database.Host,
                Port = porta,
                Database = config.Database.Database,
                Username = config.Database.Username,
                Password = senha,
                Timeout = 5,
                CommandTimeout = 15,
                IncludeErrorDetail = false,
                ApplicationName = "FugaTES.Desktop.Sd.Protocolo",
                // Escrita: esta e a unica conexao do desktop que grava protocolo.
                Options = "-c default_transaction_read_only=off"
            }.ConnectionString);

            // Conferir a identidade ANTES de usar: um perfil trocado nao pode virar escrita no
            // banco errado. A tabela da migracao 007S1 so existe no cluster certo.
            await using (var comando = fonte.CreateCommand(
                "select current_database(), current_user, to_regclass('fugates.protocolo_sd_ordem_venda') is not null"))
            await using (var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await leitor.ReadAsync(ct).ConfigureAwait(false))
                    return await DescartarAsync(fonte, "SD_BANCO_SEM_RESPOSTA");
                if (leitor.GetString(0) != config.Database.Database || leitor.GetString(1) != UsuarioExigido)
                    return await DescartarAsync(fonte, "SD_IDENTIDADE_DO_BANCO_DIVERGENTE");
                if (!leitor.GetBoolean(2))
                    return await DescartarAsync(fonte, "SD_TABELA_007S1_AUSENTE_NESTE_CLUSTER");
            }

            return new(fonte, null, null);
        }
        catch (OperationCanceledException) { if (fonte is not null) await fonte.DisposeAsync(); throw; }
        catch (PostgresException erro)
        { return await DescartarAsync(fonte, "SD_BANCO_SQLSTATE_" + erro.SqlState); }
        catch (Exception erro)
        { return await DescartarAsync(fonte, "SD_BANCO_" + erro.GetType().Name); }
    }

    // Descarta so a fonte: o tunel e do chamador, que pode querer tentar de novo com ele.
    private static async Task<Resultado> DescartarAsync(NpgsqlDataSource? fonte, string motivo)
    {
        if (fonte is not null) await fonte.DisposeAsync().ConfigureAwait(false);
        return new(null, null, motivo);
    }

    private static async Task<bool> PortaAtendeAsync(int porta, CancellationToken ct)
    {
        using var cliente = new TcpClient();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(1));
        try { await cliente.ConnectAsync("127.0.0.1", porta, limite.Token).ConfigureAwait(false); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private sealed class Posse(NpgsqlDataSource fonte, DesktopTunnel? tunel) : IDisposable
    {
        public void Dispose() { fonte.Dispose(); tunel?.Dispose(); }
    }
}
