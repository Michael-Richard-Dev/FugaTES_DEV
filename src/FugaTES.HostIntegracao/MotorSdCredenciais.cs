using System.Net.Http.Headers;
using System.Text;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;

namespace FugaTES.HostIntegracao;

/// <summary>
/// Credenciais do systemd para o motor SD.
///
/// Existe em vez de reusar <see cref="CarrierSystemdCredentials"/> por um motivo concreto: aquela
/// classe exige modo 0400 e abre UMA excecao, codificada, para o diretorio do receptor da Toledo.
/// Nesta VM o systemd cria as credenciais como 0440 com ACL (conferido em 01/11/2026:
/// "-r--r-----+ root root", diretorio 0550, pai 0755), entao o motor precisava ou de uma segunda
/// excecao naquela classe - que e usada por um servico em producao e de outro autor - ou da sua
/// propria leitura. A segunda opcao nao arrisca o servico alheio.
///
/// As conferencias sao as mesmas, e nenhuma foi afrouxada: diretorio exato do PROPRIO servico,
/// sem link simbolico em lugar nenhum, pai nao gravavel por grupo ou outros, tamanho limitado,
/// conteudo sem byte nulo. O valor nunca e registrado, nem em erro.
/// </summary>
public sealed class MotorSdCredenciais(string diretorio)
{
    /// <summary>
    /// As unidades do motor SD, uma por ambiente. O systemd monta as credenciais em
    /// /run/credentials/&lt;nome da unidade&gt;, entao o nome da unidade E a identidade do diretorio.
    ///
    /// Ate 06/11/2026 havia um nome so, escrito aqui como constante. O motor do QAS120 roda numa
    /// unidade de nome proprio - as duas nao podem compartilhar unidade - e recusava subir com
    /// SD_MOTOR_CREDENCIAL_DIRETORIO_INVALIDO lendo o diretorio CERTO: o dele.
    ///
    /// NAO ENFRAQUECE A GUARDA. O que ela impede e o motor ler as credenciais de OUTRO servico, e
    /// isso continua: o diretorio tem de ser exatamente o que o systemd informou para ESTE
    /// processo (CREDENTIALS_DIRECTORY), e ainda precisa ser o de uma unidade de motor SD
    /// aprovada. Um processo so recebe o diretorio da unidade sob a qual ele roda; aceitar os dois
    /// nomes nao da a um deles acesso ao do outro.
    ///
    /// Estes nomes sao os mesmos de instalar-motor-sd.sh (DEV110) e instalar-servicos-qas.sh
    /// (QAS120). Renomear uma unidade sem mexer aqui faz aquele motor parar de subir.
    /// </summary>
    public static readonly IReadOnlyList<string> ServicosAprovados =
    [
        "fugates-sd-protocolo.service",      // DEV110
        "fugates-sd-protocolo-qas.service",  // QAS120
    ];

    /// <summary>O nome da unidade do DEV110, preservado para quem ja o referenciava.</summary>
    public const string Servico = "fugates-sd-protocolo.service";

    public static readonly IReadOnlyList<string> DiretoriosAprovados =
        [.. ServicosAprovados.Select(s => "/run/credentials/" + s)];

    public static readonly IReadOnlyList<string> Exigidas =
    [
        "carrier-hml-sap-user", "carrier-hml-sap-password",
        "carrier-hml-guardian-email", "carrier-hml-guardian-password",
        "carrier-hml-guardian-signature"
    ];

    private const int TamanhoMaximo = 16384;

    public string Ler(string nome)
    {
        if (!Exigidas.Contains(nome, StringComparer.Ordinal))
            throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_NOME_INVALIDO");
        if (!OperatingSystem.IsLinux())
            throw new CarrierServiceStartupException("SD_MOTOR_SOMENTE_LINUX");

        try
        {
            // O diretorio tem de ser o que o systemd informou para ESTE processo, e tem de ser o
            // de uma unidade de motor SD aprovada. As duas conferencias juntas: a primeira
            // impede ler o diretorio de outro servico, a segunda impede que uma unidade de nome
            // qualquer se passe por motor.
            var informado = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            if (diretorio != informado
                || !DiretoriosAprovados.Contains(diretorio, StringComparer.Ordinal))
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_DIRETORIO_INVALIDO");

            var pasta = new DirectoryInfo(diretorio);
            var pai = pasta.Parent;
            if (pasta.LinkTarget is not null || pai is null || pai.LinkTarget is not null)
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_DIRETORIO_INVALIDO");

            // Dono le e atravessa; no maximo o grupo do servico tambem. Nada alem disso.
            var dono = UnixFileMode.UserRead | UnixFileMode.UserExecute;
            var comGrupo = dono | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            var modoPasta = File.GetUnixFileMode(diretorio);
            if (modoPasta != dono && modoPasta != comGrupo)
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_DIRETORIO_PERMISSIVO");

            // Pai gravavel por grupo ou por outros permitiria trocar o diretorio inteiro.
            if ((File.GetUnixFileMode(pai.FullName)
                 & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_PAI_PERMISSIVO");

            var caminho = Path.Combine(diretorio, nome);
            var arquivo = new FileInfo(caminho);
            if (!arquivo.Exists || arquivo.LinkTarget is not null
                || arquivo.Length is < 1 or > TamanhoMaximo)
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_AUSENTE_OU_INVALIDA");

            // So leitura, para o dono e no maximo para o grupo do servico. Nunca para "outros",
            // nunca gravavel: e o que o systemd cria nesta maquina (0440 com ACL).
            var somenteDono = UnixFileMode.UserRead;
            var donoEGrupo = UnixFileMode.UserRead | UnixFileMode.GroupRead;
            var modo = File.GetUnixFileMode(caminho);
            if (modo != somenteDono && modo != donoEGrupo)
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_PERMISSAO_INVALIDA");

            var segredo = File.ReadAllText(caminho, new UTF8Encoding(false, true));
            if (string.IsNullOrWhiteSpace(segredo) || segredo.Contains('\0'))
                throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_AUSENTE_OU_INVALIDA");
            // Devolvido como esta: aparar mudaria uma senha que termine em espaco.
            return segredo;
        }
        catch (CarrierServiceStartupException) { throw; }
        // Qualquer outra falha vira um codigo: a excecao original poderia carregar caminho ou
        // conteudo para o journal.
        catch { throw new CarrierServiceStartupException("SD_MOTOR_CREDENCIAL_INDISPONIVEL"); }
    }

    /// <summary>Le todas uma vez. Serve para falhar na subida, e nao na primeira volta.</summary>
    public void ConferirTodas() { foreach (var nome in Exigidas) _ = Ler(nome); }
}

/// <summary>
/// Autenticacao no SAP com as credenciais do motor. A origem e conferida a cada requisicao: um
/// redirecionamento para outro host nao leva o Basic junto.
/// </summary>
public sealed class MotorSdSapAuthentication(Uri aprovada, MotorSdCredenciais credenciais)
    : IAutenticacaoSap
{
    private const string UsuarioEsperado = "FUGATES";

    public Task AplicarAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (request.RequestUri is not { Scheme: "https" } destino
            || destino.GetLeftPart(UriPartial.Authority) != aprovada.GetLeftPart(UriPartial.Authority)
            || destino.UserInfo.Length != 0)
            throw new CarrierServiceStartupException("SD_MOTOR_SAP_ORIGEM_INVALIDA");

        var usuario = credenciais.Ler("carrier-hml-sap-user");
        if (usuario != UsuarioEsperado)
            throw new CarrierServiceStartupException("SD_MOTOR_SAP_USUARIO_INESPERADO");
        var senha = credenciais.Ler("carrier-hml-sap-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(usuario + ":" + senha)));
        return Task.CompletedTask;
    }
}

/// <summary>Credenciais do Guardian para o motor, pelas mesmas conferencias.</summary>
public sealed class MotorSdGuardianCredenciais(MotorSdCredenciais credenciais)
    : ICredenciaisGuardianSet26Provider
{
    public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new CredenciaisGuardianSet26(
            credenciais.Ler("carrier-hml-guardian-email"),
            credenciais.Ler("carrier-hml-guardian-password"),
            credenciais.Ler("carrier-hml-guardian-signature")));
    }
}
