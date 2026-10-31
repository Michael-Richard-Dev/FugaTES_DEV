using FugaTES.Callback.Http.ManualRetry;

namespace R158.SapInboundReadiness.Tests;

// R163: a credencial Guardian do servico 18763 deixa de depender da variavel de ambiente do
// processo, que e legivel por qualquer processo do mesmo usuario via /proc/<pid>/environ.
// Offline: nenhum servico e iniciado, nenhuma credencial real e lida, nada e rotacionado.
public sealed class GuardianSecretRemediationTests
{
    private static AcmManualRetryCommandProfile Perfil(string? arquivoSegredos = null) => new()
    {
        BaseConfigurationFile = "/tmp/base.json",
        CredentialFile = "/tmp/command.key",
        ApprovalsFile = "/tmp/approvals.json",
        Passfile = "/tmp/pgpass",
        OperatorSid = "S-1-5-21-0-0-0-1001",
        WorkerIdentity = "acm-manual-retry-dedicated",
        GuardianSecretsFile = arquivoSegredos
    };

    private static void ComAmbiente(Action corpo, params (string Nome, string? Valor)[] variaveis)
    {
        var anteriores = variaveis.Select(v => (v.Nome, Antigo: Environment.GetEnvironmentVariable(v.Nome))).ToArray();
        try
        {
            foreach (var (nome, valor) in variaveis) Environment.SetEnvironmentVariable(nome, valor);
            corpo();
        }
        finally
        {
            foreach (var (nome, antigo) in anteriores) Environment.SetEnvironmentVariable(nome, antigo);
        }
    }

    [Fact]
    public void Sem_arquivo_configurado_o_comportamento_anterior_e_preservado()
    {
        // Compatibilidade: enquanto o operador nao provisionar o arquivo, o host le o ambiente
        // exatamente como antes. Valores sinteticos; nenhuma credencial real participa.
        ComAmbiente(() =>
        {
            var lidas = AcmManualRetryCommandHost.LerCredenciaisGuardian(Perfil());
            Assert.Equal(3, lidas.Count);
            Assert.Equal("sintetico@invalid", lidas.Single(p => p.Nome == "Email").Valor);
            Assert.Equal("SENHA_SINTETICA", lidas.Single(p => p.Nome == "Password").Valor);
            Assert.Equal("SINTETICA", lidas.Single(p => p.Nome == "Signature").Valor);
        },
        ("Guardian__Email", "sintetico@invalid"),
        ("Guardian__Password", "SENHA_SINTETICA"),
        ("Guardian__Signature", "SINTETICA"));
    }

    [Fact]
    public void Variaveis_ausentes_no_ambiente_nao_viram_entradas_vazias()
    {
        ComAmbiente(() =>
        {
            var lidas = AcmManualRetryCommandHost.LerCredenciaisGuardian(Perfil());
            Assert.Single(lidas);
            Assert.Equal("Email", lidas[0].Nome);
        },
        ("Guardian__Email", "sintetico@invalid"),
        ("Guardian__Password", null),
        ("Guardian__Signature", null));
    }

    [Fact]
    public void Com_arquivo_configurado_o_ambiente_deixa_de_ser_consultado()
    {
        // O arquivo passa pelo validador de credencial privada (PrivateCommandFile), que exige
        // Linux, modo 0600, ausencia de symlink e dono conferido. Fora da VM ele recusa — e o
        // ponto do teste e justamente provar que NAO ha retorno silencioso ao ambiente.
        ComAmbiente(() =>
        {
            var erro = Assert.Throws<InvalidOperationException>(
                () => AcmManualRetryCommandHost.LerCredenciaisGuardian(Perfil("/etc/fugates/guardian.json")));
            Assert.Equal("ACM_COMMAND_LINUX_VM_REQUIRED", erro.Message);
        },
        ("Guardian__Email", "NAO_DEVE_SER_USADO"),
        ("Guardian__Password", "NAO_DEVE_SER_USADO"),
        ("Guardian__Signature", "NAO_DEVE_SER_USADO"));
    }

    [Fact]
    public void O_contrato_do_arquivo_restrito_exige_modo_privado_e_origem_externa()
    {
        var fonte = Fonte("PrivateCommandFile.cs");
        // Recusa leitura por grupo/outros, symlink, caminho dentro do release e dono inesperado.
        foreach (var regra in new[]
        {
            "ACM_COMMAND_PRIVATE_MODE_REQUIRED", "ACM_COMMAND_SYMLINK_REJECTED",
            "ACM_COMMAND_EXTERNAL_PRIVATE_FILE_REQUIRED", "ACM_COMMAND_TRUSTED_OWNER_REQUIRED"
        })
            Assert.Contains(regra, fonte, StringComparison.Ordinal);
        Assert.Contains("UnixFileMode.GroupRead | UnixFileMode.OtherRead", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void O_host_passou_a_usar_o_leitor_de_credencial_privada()
    {
        var fonte = Fonte("AcmManualRetryCommandHost.cs");
        Assert.Contains("LerCredenciaisGuardian", fonte, StringComparison.Ordinal);
        Assert.Contains("PrivateCommandFile.Read(profile.GuardianSecretsFile!", fonte, StringComparison.Ordinal);
        // O acesso direto ao ambiente so permanece no ramo de compatibilidade, dentro do metodo.
        var metodo = fonte[fonte.IndexOf("LerCredenciaisGuardian", StringComparison.Ordinal)..];
        Assert.Contains("Environment.GetEnvironmentVariable(\"Guardian__\"", metodo, StringComparison.Ordinal);
        Assert.Equal(1, Contagem(fonte, "Environment.GetEnvironmentVariable(\"Guardian__\""));
    }

    private static int Contagem(string texto, string alvo)
    {
        int n = 0, i = 0;
        while ((i = texto.IndexOf(alvo, i, StringComparison.Ordinal)) >= 0) { n++; i += alvo.Length; }
        return n;
    }

    private static string Fonte(string nome)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Directory.EnumerateFiles(Path.Combine(dir!, "src"), nome, SearchOption.AllDirectories).Single());
    }
}
