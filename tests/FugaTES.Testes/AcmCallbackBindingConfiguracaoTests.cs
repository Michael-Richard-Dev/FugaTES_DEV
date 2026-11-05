using FugaTES.Aplicacao.Servicos.Mvp;
using Microsoft.Extensions.Configuration;

namespace FugaTES.Testes;

/// <summary>
/// As chaves de callback, como SAO ESCRITAS no arquivo de ambiente da VM, tem de produzir uma
/// politica aprovada.
///
/// Este teste existe por um motivo especifico: <see cref="AcmR104CallbackBinding"/> e um record
/// POSICIONAL, e o binder de configuracao precisa montar o objeto pelo construtor. Se ele nao
/// conseguir, <c>CallbackBinding</c> fica NULO e
/// <see cref="AcmR104CallbackPolicy.Validar"/> devolve false - produzindo exatamente o mesmo
/// R165U_CALLBACK_APPROVAL_REQUIRED que aparece quando a aprovacao simplesmente nao foi dada.
/// Dois defeitos muito diferentes com o mesmo sintoma; so um teste distingue.
///
/// As chaves usam ':' porque e assim que o systemd as entrega: ele passa 'A__B' no ambiente e o
/// provedor de variaveis de ambiente traduz '__' para ':'.
/// </summary>
public sealed class AcmCallbackBindingConfiguracaoTests
{
    private const string Url = "http://192.168.17.40/api/guardian/set26/callback/recebimento";
    private const string Evidencia = "R173-CALLBACK-CONFERIDO-20261105";

    private static Dictionary<string, string?> Chaves() => new()
    {
        ["AcmR57:Normal:CallbackApproved"] = "true",
        ["AcmR57:Normal:CallbackUrl"] = Url,
        ["AcmR57:Normal:CallbackEvidence"] = Evidencia,
        ["AcmR57:Normal:CallbackBinding:Environment"] = "HML",
        ["AcmR57:Normal:CallbackBinding:Url"] = Url,
        ["AcmR57:Normal:CallbackBinding:SourceCidr"] = "192.168.17.91/32",
        ["AcmR57:Normal:CallbackBinding:SourceAllowlistVerified"] = "true",
        ["AcmR57:Normal:CallbackBinding:PreCadastroReturnRouteBindingVerified"] = "true",
        ["AcmR57:Normal:CallbackBinding:EvidenceReference"] = Evidencia
    };

    private static AcmR57NormalOptions Ligar(Dictionary<string, string?> chaves) =>
        new ConfigurationBuilder().AddInMemoryCollection(chaves).Build()
            .GetSection("AcmR57:Normal").Get<AcmR57NormalOptions>()
        ?? throw new InvalidOperationException("a secao AcmR57:Normal nao ligou");

    [Fact]
    public void O_binder_monta_o_binding_posicional()
    {
        var opcoes = Ligar(Chaves());

        Assert.True(opcoes.CallbackApproved);
        Assert.Equal(Url, opcoes.CallbackUrl);
        Assert.Equal(Evidencia, opcoes.CallbackEvidence);

        var binding = opcoes.CallbackBinding;
        Assert.NotNull(binding);
        Assert.Equal("HML", binding!.Environment);
        Assert.Equal(Url, binding.Url);
        Assert.Equal("192.168.17.91/32", binding.SourceCidr);
        Assert.True(binding.SourceAllowlistVerified);
        Assert.True(binding.PreCadastroReturnRouteBindingVerified);
        Assert.Equal(Evidencia, binding.EvidenceReference);
    }

    [Fact]
    public void As_chaves_do_ambiente_aprovam_a_politica()
    {
        var opcoes = Ligar(Chaves());
        Assert.True(AcmR104CallbackPolicy.Validar(opcoes.CallbackUrl!, opcoes.CallbackApproved,
            opcoes.CallbackBinding));
    }

    // Sem o binding, a politica recusa - e recusa com o MESMO codigo de quem nao aprovou nada.
    // E por isso que o teste acima vale: ele separa "nao aprovei" de "aprovei e nao ligou".
    [Fact]
    public void Sem_o_binding_a_politica_recusa()
        => Assert.False(AcmR104CallbackPolicy.Validar(Url, approved: true, binding: null));

    [Theory]
    [InlineData("AcmR57:Normal:CallbackBinding:Environment", "PRD")]
    [InlineData("AcmR57:Normal:CallbackBinding:SourceCidr", "192.168.17.0/24")]
    [InlineData("AcmR57:Normal:CallbackBinding:SourceAllowlistVerified", "false")]
    [InlineData("AcmR57:Normal:CallbackBinding:PreCadastroReturnRouteBindingVerified", "false")]
    [InlineData("AcmR57:Normal:CallbackBinding:EvidenceReference", "")]
    [InlineData("AcmR57:Normal:CallbackApproved", "false")]
    public void Qualquer_desvio_no_binding_reprova(string chave, string valor)
    {
        var chaves = Chaves();
        chaves[chave] = valor;
        var opcoes = Ligar(chaves);
        Assert.False(AcmR104CallbackPolicy.Validar(opcoes.CallbackUrl ?? Url, opcoes.CallbackApproved,
            opcoes.CallbackBinding));
    }

    // A excecao de HTTP vale para UMA url. Outro host, porta, caminho ou query nao entra nela.
    [Theory]
    [InlineData("http://192.168.17.40/api/guardian/set26/callback/venda")]
    [InlineData("http://192.168.17.41/api/guardian/set26/callback/recebimento")]
    [InlineData("http://192.168.17.40:8080/api/guardian/set26/callback/recebimento")]
    [InlineData("http://192.168.17.40/api/guardian/set26/callback/recebimento?x=1")]
    public void Outra_url_em_http_nao_entra_na_excecao(string url)
    {
        var chaves = Chaves();
        chaves["AcmR57:Normal:CallbackUrl"] = url;
        chaves["AcmR57:Normal:CallbackBinding:Url"] = url;
        var opcoes = Ligar(chaves);
        Assert.False(AcmR104CallbackPolicy.Validar(url, opcoes.CallbackApproved, opcoes.CallbackBinding));
    }
}
