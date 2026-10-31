using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;

namespace R158.SapInboundReadiness.Tests;

// R166: o receptor SAP DEV precisa de ExternalMode=Guardian para satisfazer o guard de admissão
// SAP, mas não executa Guardian algum. Exigir credencial na partida obrigaria a provisionar um
// segredo que nunca seria usado. Estes testes fixam que o diferimento é estreito e que o controle
// real — a recusa no ponto de uso — continua intacto.
public sealed class GuardianStartupCredentialTests
{
    private static IConfiguration Config(params (string Chave, string? Valor)[] pares)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pares.ToDictionary(p => p.Chave, p => p.Valor))
            .Build();

    [Fact]
    public void Sem_opt_in_explicito_a_credencial_continua_exigida_na_partida()
    {
        Assert.False(GuardianRuntimeComposition.CredenciaisPodemSerDiferidas(Config()));
        Assert.False(GuardianRuntimeComposition.CredenciaisPodemSerDiferidas(
            Config(("Guardian:CredentialsDeferredUntilUse", "false"))));
        // Um valor não booleano não vale como opt-in.
        Assert.False(GuardianRuntimeComposition.CredenciaisPodemSerDiferidas(
            Config(("Guardian:CredentialsDeferredUntilUse", "sim"))));
    }

    [Fact]
    public void Com_opt_in_e_nenhuma_execucao_autorizada_o_diferimento_e_permitido()
        => Assert.True(GuardianRuntimeComposition.CredenciaisPodemSerDiferidas(Config(
            ("Guardian:CredentialsDeferredUntilUse", "true"),
            ("AcmR57:Normal:ExecutionAuthorized", "false"),
            ("AcmProtocolCommand:ExecutionAuthorized", "false"),
            ("FUGATES_INTEGRATION_WORKER_ENABLED", "false"))));

    // Qualquer execução Guardian ligada recoloca a exigência na partida, mesmo com o opt-in.
    [Theory]
    [InlineData("AcmR57:Normal:ExecutionAuthorized")]
    [InlineData("AcmProtocolCommand:ExecutionAuthorized")]
    [InlineData("FUGATES_INTEGRATION_WORKER_ENABLED")]
    public void Qualquer_execucao_autorizada_volta_a_exigir_credencial(string chave)
        => Assert.False(GuardianRuntimeComposition.CredenciaisPodemSerDiferidas(Config(
            ("Guardian:CredentialsDeferredUntilUse", "true"),
            (chave, "true"))));

    // O controle que importa: sem credencial, nenhuma chamada Guardian acontece.
    [Fact]
    public async Task Sem_credencial_o_provider_recusa_fechado_no_ponto_de_uso()
    {
        var provider = new GuardianRuntimeCredenciaisProvider(Config(
            ("Guardian:CredentialsDeferredUntilUse", "true")));
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ObterAsync(CancellationToken.None));
        Assert.Equal("Guardian runtime credentials are absent.", erro.Message);
    }

    // E com credencial presente o provider segue entregando normalmente: o diferimento não
    // interfere em host que de fato fala com o Guardian.
    [Fact]
    public async Task Com_credencial_presente_o_provider_entrega_normalmente()
    {
        var provider = new GuardianRuntimeCredenciaisProvider(Config(
            ("Guardian:Email", "sintetico@invalid"),
            ("Guardian:Password", "SINTETICO"),
            ("Guardian:Signature", "SINTETICA")));
        var credenciais = await provider.ObterAsync(CancellationToken.None);
        Assert.Equal("sintetico@invalid", credenciais.Usuario);
        Assert.Equal("SINTETICO", credenciais.Senha);
        Assert.Equal("SINTETICA", credenciais.Assinatura);
    }

    // A configuração entregue do receptor usa o diferimento e NÃO carrega credencial alguma.
    [Fact]
    public void A_configuracao_entregue_do_receptor_nao_carrega_credencial_guardian()
    {
        var caminho = Path.Combine(Raiz(), "deploy", "sap-inbound-dev", "appsettings.receiver.dev.json");
        var configuracao = new ConfigurationBuilder().AddJsonFile(caminho, optional: false).Build();

        Assert.True(GuardianRuntimeComposition.CredenciaisPodemSerDiferidas(configuracao));
        foreach (var chave in new[] { "Guardian:Email", "Guardian:Password", "Guardian:Signature" })
            Assert.True(string.IsNullOrWhiteSpace(configuracao[chave]), chave + " não deveria existir");
        // E o modo externo exigido pelo guard SAP continua declarado.
        Assert.Equal("Guardian", configuracao["HostIntegracao:ExternalMode"]);
    }

    private static string Raiz()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
