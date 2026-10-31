using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.WinForms.Monitor;

namespace R158.SapInboundReadiness.Tests;

// R160: guardas de conexao da identidade de escrita proposta e contrato de ciclo de vida do
// tunel SSH local. Tudo OFFLINE: nenhuma conexao de banco, nenhum processo iniciado ou encerrado.
public sealed class SapWriterAndTunnelTests
{
    // ---------- 5) a role nova herda as MESMAS guardas de conexao ----------

    private static string Conexao(string role, int porta = 5433, string database = "fugates_dev",
        string schema = "fugates", string host = "127.0.0.1")
        => $"Host={host};Port={porta};Database={database};Username={role};Search Path={schema};" +
           "Passfile=/home/michael/.fugates-acm-r165p/pgpass";

    [Fact]
    public void As_tres_identidades_aprovadas_sao_guardadas()
    {
        Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(NpgsqlDataSourceFactory.AcmApplicationRole));
        Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(NpgsqlDataSourceFactory.SapInboundApplicationRole));
        Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(NpgsqlDataSourceFactory.DevReadOnlyApplicationRole));
        Assert.Equal("fugates_sap_inbound_app", NpgsqlDataSourceFactory.SapInboundApplicationRole);
        Assert.Equal("fugates_dev_read_app", NpgsqlDataSourceFactory.DevReadOnlyApplicationRole);
        // As tres identidades sao distintas entre si: operacional compartilhada, escrita SAP e
        // leitura GET-only. A separacao e o proprio objetivo do isolamento.
        var todas = new[] { NpgsqlDataSourceFactory.AcmApplicationRole,
                            NpgsqlDataSourceFactory.SapInboundApplicationRole,
                            NpgsqlDataSourceFactory.DevReadOnlyApplicationRole };
        Assert.Equal(3, todas.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("postgres")]
    [InlineData("fugates_dev_app")]
    [InlineData("fugates_dev_owner")]
    public void Identidades_nao_aprovadas_nao_sao_guardadas(string? role)
        => Assert.False(NpgsqlDataSourceFactory.EhRoleGuardada(role));

    [Theory]
    [InlineData("fugates_acm_hml_app")]
    [InlineData("fugates_sap_inbound_app")]
    [InlineData("fugates_dev_read_app")]
    public void Destino_divergente_e_recusado_para_ambas_as_roles(string role)
    {
        // Porta, base, schema e host sao verificados antes de qualquer conexao, para as duas.
        foreach (var conexao in new[]
        {
            Conexao(role, porta: 5432), Conexao(role, porta: 5434), Conexao(role, porta: 15434),
            Conexao(role, database: "fugates_hml"), Conexao(role, schema: "public"),
            Conexao(role, host: "10.0.0.9")
        })
        {
            var erro = Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.CriarAcm(conexao));
            Assert.Equal("R165R_ACM_CONNECTION_METADATA_REQUIRED", erro.Message);
        }
    }

    [Theory]
    [InlineData("fugates_acm_hml_app")]
    [InlineData("fugates_sap_inbound_app")]
    [InlineData("fugates_dev_read_app")]
    public void Passfile_explicito_continua_obrigatorio_para_ambas(string role)
    {
        // Senha embutida na connection string nunca e aceita.
        var comSenha = $"Host=127.0.0.1;Port=5433;Database=fugates_dev;Username={role};Search Path=fugates;Password=x";
        var erro = Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.CriarAcm(comSenha));
        Assert.Equal("R165R_EXPLICIT_ACM_PASSFILE_REQUIRED", erro.Message);
    }

    [Fact]
    public void O_caminho_generico_nao_contorna_as_guardas_da_role_de_escrita()
    {
        // Criar() precisa rotear a role de escrita para CriarAcm, igual a leitora.
        var erro = Assert.Throws<InvalidOperationException>(() =>
            NpgsqlDataSourceFactory.Criar(Conexao(NpgsqlDataSourceFactory.SapInboundApplicationRole, porta: 5432)));
        Assert.Equal("R165R_ACM_CONNECTION_METADATA_REQUIRED", erro.Message);
    }

    [Fact]
    public void A_sonda_de_identidade_fisica_recusa_role_fora_da_lista_guardada()
    {
        // Sem abrir conexao: a validacao da role esperada ocorre antes de qualquer I/O.
        var erro = Assert.ThrowsAsync<FugaTES.Aplicacao.Servicos.Mvp.AcmR57BlockedException>(
            () => AcmPostgreSqlIdentityGuard.VerifyAsync(null!, default, "postgres")).Result;
        Assert.Equal("R146E_PHYSICAL_DATABASE_IDENTITY_MISMATCH", erro.Message);
    }

    // ---------- 8) ciclo de vida do tunel SSH local ----------

    private static LocalReadTunnelOptions Perfil(int remota = 18762) => new()
    {
        Enabled = true,
        Host = "192.168.17.40",
        User = "michael",
        IdentityFile = @"C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256",
        KnownHostsFile = @"C:\Users\TI\.ssh\known_hosts",
        RemoteReadPort = remota
    };

    [Fact]
    public void O_encaminhamento_fixa_a_porta_local_e_usa_a_remota_do_perfil()
    {
        var args = LocalReadTunnel.BuildArguments(Perfil());
        Assert.Contains("-L", args);
        Assert.Contains("127.0.0.1:18761:127.0.0.1:18762", args);
        // Nenhum comando remoto e enviado: tunel puro.
        Assert.Contains("-N", args);
        Assert.Contains("-T", args);
        Assert.Contains("ExitOnForwardFailure=yes", args);
        // O perfil anterior (18761 remoto) continua suportado.
        Assert.Contains("127.0.0.1:18761:127.0.0.1:18761", LocalReadTunnel.BuildArguments(Perfil(18761)));
    }

    [Fact]
    public void Um_ssh_de_terceiros_nunca_e_reconhecido_como_tunel_do_fugates()
    {
        var perfil = Perfil();
        var aprovados = LocalReadTunnel.BuildArguments(perfil);
        // O proprio comando aprovado e reconhecido.
        Assert.True(LocalReadTunnel.IsApprovedCommand(LocalReadTunnel.SshExecutable, aprovados, perfil));

        // Executavel diferente: recusado, mesmo com os argumentos certos.
        Assert.False(LocalReadTunnel.IsApprovedCommand(@"C:\Ferramentas\ssh.exe", aprovados, perfil));

        // Encaminhamento para outra porta remota: recusado.
        var outraPorta = aprovados.Select(a => a == "127.0.0.1:18761:127.0.0.1:18762"
            ? "127.0.0.1:18761:127.0.0.1:22" : a).ToArray();
        Assert.False(LocalReadTunnel.IsApprovedCommand(LocalReadTunnel.SshExecutable, outraPorta, perfil));

        // Argumento extra: recusado.
        Assert.False(LocalReadTunnel.IsApprovedCommand(LocalReadTunnel.SshExecutable,
            [.. aprovados, "-D", "1080"], perfil));

        // Identidade diferente: recusado.
        var outraIdentidade = aprovados.Select(a => a == perfil.IdentityFile ? @"C:\outra\chave" : a).ToArray();
        Assert.False(LocalReadTunnel.IsApprovedCommand(LocalReadTunnel.SshExecutable, outraIdentidade, perfil));
    }

    [Theory]
    [InlineData("10.0.0.1", "michael", 18762)]
    [InlineData("192.168.17.40", "outro", 18762)]
    [InlineData("192.168.17.40", "michael", 22)]
    public void Perfil_de_tunel_divergente_e_recusado(string host, string user, int remota)
    {
        var perfil = Perfil(remota) with { Host = host, User = user };
        Assert.Throws<PhysicalConnectionException>(() => LocalReadTunnel.BuildArguments(perfil));
    }

    [Fact]
    public void O_tunel_so_encerra_o_processo_que_ele_mesmo_iniciou()
    {
        // Contrato de propriedade: Dispose nunca mata um tunel reutilizado de terceiros.
        var fonte = Fonte("LocalReadTunnel.cs");
        Assert.Contains("if(owned && verified is {HasExited:false})verified.Kill();", fonte, StringComparison.Ordinal);
        Assert.Contains("Reuse verified existing tunnel; it remains externally owned.", fonte, StringComparison.Ordinal);
        // E a sessao do Desktop descarta o tunel no encerramento normal.
        Assert.Contains("tunnel?.Dispose();", Fonte("UnifiedDesktopSession.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void O_script_de_limpeza_so_encerra_um_tunel_comprovadamente_do_fugates()
    {
        var script = File.ReadAllText(Path.Combine(Raiz(), "deploy", "sap-inbound-dev", "Encerrar-TunelRemanescente.ps1"));
        // Tres criterios cumulativos antes de encerrar qualquer processo.
        Assert.Contains("REFUSED_NOT_FUGATES", script, StringComparison.Ordinal);
        Assert.Contains("System32\\OpenSSH\\ssh.exe", script, StringComparison.Ordinal);
        Assert.Contains("-L 127.0.0.1:{0}:127.0.0.1:{1}", script, StringComparison.Ordinal);
        Assert.Contains("IdentityFile", script, StringComparison.Ordinal);
        // Nunca encerra enquanto o aplicativo dono estiver em execucao.
        Assert.Contains("SKIPPED_APP_RUNNING", script, StringComparison.Ordinal);
        // Nao encerra processos em lote nem por nome.
        Assert.DoesNotContain("Get-Process ssh", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Name", script, StringComparison.Ordinal);
    }

    // ---------- apoio ----------

    private static string Raiz()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    private static string Fonte(string nome)
        => File.ReadAllText(Directory.EnumerateFiles(Path.Combine(Raiz(), "src"), nome, SearchOption.AllDirectories).Single());
}
