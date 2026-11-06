using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Callback.Http;
using FugaTES.Integracao.SAP.Autenticacao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O ENVIO TOLEDO-&gt;SAP SO ACONTECE NO AMBIENTE EM QUE FOI AUTORIZADO.
///
/// Ate 06/11/2026 a conferencia era "Environment != DEV, entao recusa". Isso impedia o QAS120 de
/// enviar - e, no dia em que o QAS declarasse "DEV" para destravar, nao impediria nada: so o NOME
/// era conferido, nunca contra o SAP com quem o processo realmente conversa.
///
/// Agora o par e indivisivel: o nome declarado tem de corresponder ao SAP real. O operador
/// autorizou o QAS120 em 06/11/2026; a referencia da autorizacao continua sendo registro escrito
/// no ambiente, e nao lista no codigo - fixar as referencias aceitas exigiria saber a do DEV em
/// producao, e supor errado faria o receptor do DEV recusar subir.
/// </summary>
public sealed class ToledoSapReturnAmbienteTests
{
    private sealed class AutenticacaoFalsa : IAutenticacaoSap
    {
        public Task<string> ObterTokenAsync(CancellationToken ct) => Task.FromResult("");
        public Task AplicarAsync(HttpRequestMessage requisicao, CancellationToken ct) => Task.CompletedTask;
    }

    private const string SapDev = "https://vhfufds4ci.sap.fugacouros.com.br:44300";
    private const string SapQas = "https://vhfufqs4ci.sap.fugacouros.com.br:44300";

    private static Dictionary<string, string?> Perfil(string declarado, string sapUrl, string sapClient) => new()
    {
        ["ToledoSapReturn:Enabled"] = "true",
        ["ToledoSapReturn:Environment"] = declarado,
        ["ToledoSapReturn:ExecutionAuthorized"] = "true",
        ["ToledoSapReturn:AutomaticProcessingEnabled"] = "true",
        ["ToledoSapReturn:OriginalDocumentApproved"] = "true",
        ["ToledoSapReturn:NotBeforeUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        ["ToledoSapReturn:UntilUtc"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
        ["ToledoSapReturn:ApprovedSourceIdentity"] = "OFFLINE_APPROVED_SOURCE",
        ["ToledoSapReturn:AuthorizationReference"] = "OFFLINE_USER_APPROVAL",
        ["Sap:BaseUrl"] = sapUrl,
        ["Sap:SapClient"] = sapClient,
    };

    private static Exception? Montar(string declarado, string sapUrl, string sapClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAutenticacaoSap, AutenticacaoFalsa>();
        return Record.Exception(() => ToledoSapReturnComposition.Configure(
            services, new ConfigurationBuilder().AddInMemoryCollection(Perfil(declarado, sapUrl, sapClient)).Build()));
    }

    /// <summary>Cada ambiente declarando o SEU proprio SAP: autorizado.</summary>
    [Theory]
    [InlineData("DEV", SapDev, "110")]
    [InlineData("QAS120", SapQas, "120")]
    public void O_ambiente_que_corresponde_ao_sap_real_e_autorizado(string declarado, string url, string client)
        => Assert.Null(Montar(declarado, url, client));

    /// <summary>
    /// O CRUZAMENTO: declarar um ambiente e falar com o SAP do outro. E o caso que a conferencia
    /// antiga nao pegava - ela olhava so o nome, entao "DEV" com o SAP do QAS passava.
    /// </summary>
    [Theory]
    [InlineData("DEV", SapQas, "120")]
    [InlineData("QAS120", SapDev, "110")]
    public void Declarar_um_ambiente_e_falar_com_o_sap_do_outro_e_recusado(string declarado, string url, string client)
        => Assert.Equal("TOLEDO_SAP_EXPLICIT_ACTIVATION_REQUIRED", Montar(declarado, url, client)?.Message);

    /// <summary>Nome que nao e de ambiente aprovado nenhum continua recusado.</summary>
    [Theory]
    [InlineData("HML")]
    [InlineData("DEV110")]
    [InlineData("PRD")]
    [InlineData("")]
    public void Nome_fora_da_lista_e_recusado(string declarado)
        => Assert.Equal("TOLEDO_SAP_EXPLICIT_ACTIVATION_REQUIRED", Montar(declarado, SapDev, "110")?.Message);

    /// <summary>
    /// Par SAP que nao e de ambiente aprovado: o host certo com o client do outro nao vira
    /// ambiente, entao nao ha a que corresponder.
    /// </summary>
    [Theory]
    [InlineData("DEV", SapDev, "120")]
    [InlineData("QAS120", SapQas, "110")]
    public void Sap_de_par_invalido_nao_autoriza_ninguem(string declarado, string url, string client)
        => Assert.Equal("TOLEDO_SAP_EXPLICIT_ACTIVATION_REQUIRED", Montar(declarado, url, client)?.Message);

    /// <summary>
    /// As demais exigencias de ativacao seguem valendo. Alargar o ambiente nao podia afrouxar o
    /// que o operador precisa declarar explicitamente.
    /// </summary>
    [Theory]
    [InlineData("ToledoSapReturn:ExecutionAuthorized")]
    [InlineData("ToledoSapReturn:AutomaticProcessingEnabled")]
    [InlineData("ToledoSapReturn:OriginalDocumentApproved")]
    public void As_demais_exigencias_continuam(string chave)
    {
        var perfil = Perfil("QAS120", SapQas, "120");
        perfil[chave] = "false";
        var services = new ServiceCollection();
        services.AddSingleton<IAutenticacaoSap, AutenticacaoFalsa>();

        var erro = Record.Exception(() => ToledoSapReturnComposition.Configure(
            services, new ConfigurationBuilder().AddInMemoryCollection(perfil).Build()));

        Assert.Equal("TOLEDO_SAP_EXPLICIT_ACTIVATION_REQUIRED", erro?.Message);
    }
}
