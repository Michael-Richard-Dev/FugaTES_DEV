using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Integracao.Toledo.Fakes;

namespace FugaTES.Testes;

public sealed class Inc002ClienteSapEmissorToledoTests
{
    [Fact]
    public async Task ClientePjSintetico_MaterializaBaselineEmissorFechadoPorArgos()
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(ClientePjSintetico()));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);
        var json = JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!;
        var entidade = json["entidadeEmissor"]!;

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(1, emissor.QuantidadeChamadas);
        Assert.Equal("BP-SINTETICO-002", sap.BusinessPartnerRecebido);
        Assert.Equal("", (string?)json["voConfiguracao"]!["urlParaRetorno"]);
        Assert.Equal("BP-SINTETICO-002", (string?)entidade["codigo"]);
        Assert.Equal(2, (int?)entidade["tipoEmissor"]);
        Assert.Equal("Cliente PJ Sintetico", (string?)entidade["razaoSocial"]);
        Assert.Equal("BR1-PJ-SINTETICO", (string?)entidade["cnpj"]);
        Assert.Equal(303405, (int?)entidade["tipoDocumento"]);
    }

    [Fact]
    public async Task ClientePjSintetico_E2EPassaDoPayloadSapParaRequestEmissorToledo()
    {
        var handler = new HandlerHttpSapFake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PayloadSapClientePjSintetico(), Encoding.UTF8, "application/json")
        }));
        var clienteHttp = new ClienteHttpParceiroNegocioSap(
            new HttpClient(handler),
            new OpcoesSap { BaseUrl = new UriBuilder("https", "example.invalid").Uri, Timeout = TimeSpan.FromSeconds(30) },
            new AutenticacaoSapNoOp());
        var sap = new ConsultaClienteSapAdapter(clienteHttp, TimeProvider.System);
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);
        var entidade = JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!["entidadeEmissor"]!;

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(1, emissor.QuantidadeChamadas);
        Assert.Equal("BP-SINTETICO-002", (string?)entidade["codigo"]);
        Assert.Equal("Cliente PJ Sintetico", (string?)entidade["razaoSocial"]);
        Assert.Equal("BR1-PJ-SINTETICO", (string?)entidade["cnpj"]);
        Assert.Equal(303405, (int?)entidade["tipoDocumento"]);
        Assert.Equal("", (string?)JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!["voConfiguracao"]!["urlParaRetorno"]);
    }
    [Fact]
    public async Task PayloadSapSemBusinessPartnerFullNameComOrganizationName_FalhaFechadoENaoChamaToledo()
    {
        var sap = CriarAdapterSapRaw(PayloadSapClientePjSintetico(businessPartnerFullName: null, organizationName: "EMPRESA-FALLBACK-SINTETICA"));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task PayloadSapSemBusinessPartnerFullNameComFirstLastName_FalhaFechadoENaoChamaToledo()
    {
        var sap = CriarAdapterSapRaw(PayloadSapClientePjSintetico(businessPartnerFullName: null, firstName: "NOME-SINTETICO", lastName: "SOBRENOME-SINTETICO"));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task PayloadSapComBusinessPartnerFullNameEOrganizationNameDiferente_UsaFullNameComoRazaoSocial()
    {
        var sap = CriarAdapterSapRaw(PayloadSapClientePjSintetico(
            businessPartnerFullName: "RAZAO-AUTORITATIVA-SINTETICA",
            organizationName: "EMPRESA-FALLBACK-SINTETICA"));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);
        var entidade = JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!["entidadeEmissor"]!;

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(1, emissor.QuantidadeChamadas);
        Assert.Equal("RAZAO-AUTORITATIVA-SINTETICA", (string?)entidade["razaoSocial"]);
        Assert.NotEqual("EMPRESA-FALLBACK-SINTETICA", (string?)entidade["razaoSocial"]);
    }

    [Fact]
    public async Task NomeNormalizadoPreservaFallbackInc001ParaConsumidorSapGeral()
    {
        var sap = CriarAdapterSapRaw(PayloadSapClientePjSintetico(businessPartnerFullName: null, organizationName: "EMPRESA-FALLBACK-SINTETICA"));

        var resultado = await sap.ConsultarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.Equal("EMPRESA-FALLBACK-SINTETICA", resultado.Dados?.Nome);
        Assert.Null(resultado.Dados?.NomeCompletoParceiroNegocio);
    }
    [Fact]
    public async Task PayloadSapSemToCustomer_FalhaFechadoENaoChamaToledo()
    {
        var handler = new HandlerHttpSapFake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PayloadSapClientePjSintetico(incluirCliente: false), Encoding.UTF8, "application/json")
        }));
        var clienteHttp = new ClienteHttpParceiroNegocioSap(
            new HttpClient(handler),
            new OpcoesSap { BaseUrl = new UriBuilder("https", "example.invalid").Uri, Timeout = TimeSpan.FromSeconds(30) },
            new AutenticacaoSapNoOp());
        var sap = new ConsultaClienteSapAdapter(clienteHttp, TimeProvider.System);
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task BusinessPartnerEntradaVazia_BloqueiaAntesDoGatewayToledo()
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(ClientePjSintetico()));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        await Assert.ThrowsAsync<ArgumentException>(() => casoDeUso.ExecutarAsync(" ", CancellationToken.None));

        Assert.Equal(0, emissor.QuantidadeChamadas);
    }
    [Theory]
    [InlineData(StatusConsultaSap.NaoEncontrado)]
    [InlineData(StatusConsultaSap.CategoriaNaoCorrespondente)]
    [InlineData(StatusConsultaSap.FalhaContrato)]
    [InlineData(StatusConsultaSap.FalhaTecnica)]
    public async Task SapNaoConfirmaCliente_NaoChamaToledo(StatusConsultaSap statusSap)
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(statusSap));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task SapSucessoComCategoriaDiferente_NaoChamaToledo()
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(Parceiro(CategoriaParceiroNegocio.Transportadora, 2, [Documento(TipoDocumentoFiscal.Cnpj, "BR1-PJ-SINTETICO")])));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task BusinessPartnerCategoryUm_MapeiaTipoDocumento303404SemPromoverBr2ParaCnpj()
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(ClientePfSintetico()));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-001", CancellationToken.None);
        var entidade = JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!["entidadeEmissor"]!;

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(303404, (int?)entidade["tipoDocumento"]);
        Assert.Null(entidade["cnpj"]);
        Assert.Equal(1, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task BusinessPartnerCategoryDois_MapeiaTipoDocumento303405()
    {
        var emissor = await ExecutarSucessoAsync(ClientePjSintetico());

        Assert.Equal(303405, emissor.TipoDocumento);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    public async Task BusinessPartnerCategoryDesconhecida_FalhaFechadoENaoChamaToledo(int? categoria)
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(Parceiro(CategoriaParceiroNegocio.Cliente, categoria, [Documento(TipoDocumentoFiscal.Cnpj, "BR1-PJ-SINTETICO")])));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task ClientePjSemBr1_FalhaFechadoENaoChamaToledo()
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(Parceiro(CategoriaParceiroNegocio.Cliente, 2, [])));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task NomeAusente_FalhaFechadoENaoChamaToledo()
    {
        var cliente = ClientePjSintetico() with { NomeCompletoParceiroNegocio = " " };
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(cliente));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task UrlParaRetornoSerializaMesmoVaziaENullEomitido()
    {
        var cliente = Parceiro(CategoriaParceiroNegocio.Cliente, 2, [Documento(TipoDocumentoFiscal.Cnpj, "BR1-PJ-SINTETICO")], endereco: null, emails: []);
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(cliente));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);
        var entidade = JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!["entidadeEmissor"]!.AsObject();

        Assert.Equal("", (string?)JsonNode.Parse(emissor.UltimoRequestJsonRecebido!)!["voConfiguracao"]!["urlParaRetorno"]);
        Assert.False(entidade.ContainsKey("inscricaoEstadual"));
        Assert.False(entidade.ContainsKey("municipio"));
        Assert.False(entidade.ContainsKey("uf"));
        Assert.False(entidade.ContainsKey("cep"));
        Assert.False(entidade.ContainsKey("email"));
    }

    [Fact]
    public async Task CamposOmitidosNaoSaoMaterializadosComDefault()
    {
        var (_, json) = await ExecutarSucessoComJsonAsync(ClientePjSintetico());

        Assert.DoesNotContain("incluiAtualiza", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("descricao", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sequencial", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endereco", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("complemento", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telefone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observacao", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("estado", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CamposOpcionaisComOrigemResolvida_SaoSerializados()
    {
        var (emissor, json) = await ExecutarSucessoComJsonAsync(ClientePjSintetico());
        var entidade = JsonNode.Parse(json)!["entidadeEmissor"]!;

        Assert.Equal("BR3-IE-SINTETICA", emissor.InscricaoEstadual);
        Assert.Equal("Cidade Sintetica", (string?)entidade["municipio"]);
        Assert.Equal(26, (int?)entidade["uf"]);
        Assert.Equal("CEP-SINTETICO", (string?)entidade["cep"]);
        Assert.Equal("cliente.pj.sintetico@example.invalid", (string?)entidade["email"]);
    }

    [Fact]
    public async Task RegiaoVaziaComEndereco_FalhaFechadoENaoChamaToledo()
    {
        var cliente = Parceiro(CategoriaParceiroNegocio.Cliente, 2, [Documento(TipoDocumentoFiscal.Cnpj, "BR1-PJ-SINTETICO")], endereco: Endereco(" "));
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(cliente));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }
    [Fact]
    public async Task RegiaoDesconhecidaComEndereco_FalhaFechadoENaoChamaToledo()
    {
        var cliente = Parceiro(CategoriaParceiroNegocio.Cliente, 2, [Documento(TipoDocumentoFiscal.Cnpj, "BR1-PJ-SINTETICO")], endereco: Endereco("ZZ"));
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(cliente));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-002", CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Equal(0, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task EmissorFake_NaoAbreRedeERegistraEntradaEJson()
    {
        var emissor = new EmissorToledoGatewayFake();
        var entrada = new EmissorToledo("BP-SINTETICO-002", "Cliente Sintetico", 2, 303405, "BR1-PJ-SINTETICO", null, null, null, null, null);

        var resultado = await emissor.EnviarAsync(entrada, CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(1, emissor.QuantidadeChamadas);
        Assert.Same(entrada, emissor.UltimoEmissorRecebido);
        Assert.Contains("urlParaRetorno", emissor.UltimoRequestJsonRecebido);
    }

    [Fact]
    public async Task EmissorFake_PermiteFalhaControlada()
    {
        var emissor = new EmissorToledoGatewayFake(StatusOperacaoToledo.FalhaTecnica);
        var entrada = new EmissorToledo("BP-SINTETICO-002", "Cliente Sintetico", 2, 303405, "BR1-PJ-SINTETICO", null, null, null, null, null);

        var resultado = await emissor.EnviarAsync(entrada, CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaTecnica, resultado.Status);
        Assert.Equal(1, emissor.QuantidadeChamadas);
    }

    [Fact]
    public async Task TransportadoraFake_ContratoSeparadoDeEmissor()
    {
        var transportadoraFake = new TransportadoraToledoGatewayFake();
        var emissorFake = new EmissorToledoGatewayFake();
        var transportadora = new TransportadoraToledo("BP-TRANSPORTADORA-SINTETICA", "Transportadora Sintetica", "SC");

        var resultado = await transportadoraFake.EnviarAsync(transportadora, CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        var json = JsonNode.Parse(transportadoraFake.UltimoRequestJsonRecebido!)!;

        Assert.Equal(1, transportadoraFake.QuantidadeChamadas);
        Assert.Equal(0, emissorFake.QuantidadeChamadas);
        Assert.IsType<TransportadoraToledoRegistrada>(resultado.Dados);
        Assert.NotNull(json["entidadeTransportadora"]);
        Assert.Null(json["entidadeTransportadora"]!["tipoEmissor"]);
        Assert.Null(json["entidadeTransportadora"]!["incluiAtualiza"]);
        Assert.Null(json["voConfiguracao"]);
    }

    [Fact]
    public void Transportadora_NaoRecebeCamposFechadosSomenteParaEmissor()
    {
        var raiz = LocalizarRaizRepositorio();
        var conteudo = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.Aplicacao", "Modelos", "Toledo", "TransportadoraToledo.cs"))
            + File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Fakes", "TransportadoraToledoGatewayFake.cs"));

        Assert.DoesNotContain("TipoEmissor", conteudo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IncluiAtualiza", conteudo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UrlParaRetorno", conteudo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransportadoraFake_PermiteFalhaControladaECapturaJson()
    {
        var transportadoraFake = new TransportadoraToledoGatewayFake(StatusOperacaoToledo.FalhaTecnica);
        var transportadora = new TransportadoraToledo("BP-TRANSPORTADORA-SINTETICA", "Transportadora Sintetica", "SP");

        var resultado = await transportadoraFake.EnviarAsync(transportadora, CancellationToken.None);
        var json = JsonNode.Parse(transportadoraFake.UltimoRequestJsonRecebido!)!;

        Assert.Equal(StatusOperacaoToledo.FalhaTecnica, resultado.Status);
        Assert.Equal(1, transportadoraFake.QuantidadeChamadas);
        Assert.NotNull(json["entidadeTransportadora"]);
        Assert.Null(json["entidadeTransportadora"]!["tipoEmissor"]);
        Assert.Null(json["entidadeTransportadora"]!["incluiAtualiza"]);
    }

    [Fact]
    public void FakesToledo_NaoContemHttpOuRede()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Fakes"), "*.cs", SearchOption.AllDirectories);
        var proibidos = new[] { "HttpClient", "HttpRequestMessage", "http://", "https://", "TcpClient", "Socket" };

        foreach (var arquivo in arquivos)
        {
            var conteudo = File.ReadAllText(arquivo);
            foreach (var proibido in proibidos)
            {
                Assert.DoesNotContain(proibido, conteudo, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task AutenticacaoFake_RetornaSessaoSinteticaSemRede()
    {
        var autenticacao = new AutenticacaoToledoGatewayFake();

        var resultado = await autenticacao.AutenticarAsync(CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(1, autenticacao.QuantidadeChamadas);
        Assert.Equal("sessao-sintetica", resultado.Dados?.ValorSintetico);
    }

    [Fact]
    public async Task AutenticacaoFake_PermiteFalhaDeclarada()
    {
        var autenticacao = new AutenticacaoToledoGatewayFake(ResultadoOperacaoToledo<SessaoAutenticacaoToledo>.Falha(StatusOperacaoToledo.FalhaTecnica));

        var resultado = await autenticacao.AutenticarAsync(CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaTecnica, resultado.Status);
        Assert.Null(resultado.Dados);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AutenticacaoFake_TokenSinteticoInvalidoRetornaFalhaContrato(string? token)
    {
        var autenticacao = new AutenticacaoToledoGatewayFake(ResultadoOperacaoToledo<SessaoAutenticacaoToledo>.Sucesso(new SessaoAutenticacaoToledo(token!)));

        var resultado = await autenticacao.AutenticarAsync(CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaContrato, resultado.Status);
        Assert.Null(resultado.Dados);
    }

    private static async Task<EmissorToledo> ExecutarSucessoAsync(ParceiroNegocioConsultado cliente)
    {
        var (emissor, _) = await ExecutarSucessoComJsonAsync(cliente);
        return emissor;
    }

    private static async Task<(EmissorToledo Emissor, string Json)> ExecutarSucessoComJsonAsync(ParceiroNegocioConsultado cliente)
    {
        var sap = new ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(cliente));
        var emissor = new EmissorToledoGatewayFake();
        var casoDeUso = new EnviarClienteSapParaEmissorToledoCasoDeUso(sap, emissor);

        var resultado = await casoDeUso.ExecutarAsync(cliente.BusinessPartner, CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.NotNull(resultado.Dados);
        Assert.NotNull(emissor.UltimoEmissorRecebido);
        Assert.NotNull(emissor.UltimoRequestJsonRecebido);
        return (emissor.UltimoEmissorRecebido, emissor.UltimoRequestJsonRecebido);
    }

    private static ParceiroNegocioConsultado ClientePjSintetico()
    {
        return Parceiro(
            CategoriaParceiroNegocio.Cliente,
            2,
            [Documento(TipoDocumentoFiscal.Cnpj, "BR1-PJ-SINTETICO"), Documento(TipoDocumentoFiscal.InscricaoEstadual, "BR3-IE-SINTETICA")],
            Endereco("SP"),
            ["cliente.pj.sintetico@example.invalid"]);
    }

    private static ParceiroNegocioConsultado ClientePfSintetico()
    {
        return Parceiro(
            CategoriaParceiroNegocio.Cliente,
            1,
            [Documento(TipoDocumentoFiscal.Cpf, "BR2-PF-SINTETICO")],
            null,
            []);
    }

    private static string PayloadSapClientePjSintetico(
        bool incluirCliente = true,
        string? businessPartnerFullName = "Cliente PJ Sintetico",
        string? organizationName = null,
        string? firstName = null,
        string? lastName = null)
    {
        var referencia = DateTimeOffset.UtcNow;
        var d = new Dictionary<string, object?>
        {
            ["BusinessPartner"] = "BP-SINTETICO-002",
            ["BusinessPartnerCategory"] = 2,
            ["BusinessPartnerFullName"] = businessPartnerFullName,
            ["OrganizationBPName1"] = organizationName,
            ["FirstName"] = firstName,
            ["LastName"] = lastName,
            ["BusinessPartnerIsBlocked"] = "false",
            ["to_Customer"] = incluirCliente ? new Dictionary<string, object?> { ["Customer"] = "CLIENTE-PJ-SINTETICO" } : null,
            ["to_Supplier"] = null,
            ["to_BusinessPartnerRole"] = ColecaoSap([]),
            ["to_BusinessPartnerTax"] = ColecaoSap([
                new Dictionary<string, object?> { ["BPTaxType"] = "BR1", ["BPTaxNumber"] = "BR1-PJ-SINTETICO", ["BPTaxLongNumber"] = null },
                new Dictionary<string, object?> { ["BPTaxType"] = "BR3", ["BPTaxNumber"] = "BR3-IE-SINTETICA", ["BPTaxLongNumber"] = null }
            ]),
            ["to_BusinessPartnerAddress"] = ColecaoSap([
                new Dictionary<string, object?>
                {
                    ["AddressID"] = "END-SINTETICO-001",
                    ["StreetName"] = "Rua Sintetica",
                    ["CityName"] = "Cidade Sintetica",
                    ["Region"] = "SP",
                    ["PostalCode"] = "CEP-SINTETICO",
                    ["Country"] = "BR",
                    ["to_AddressUsage"] = ColecaoSap([new Dictionary<string, object?> { ["AddressID"] = "END-SINTETICO-001", ["AddressUsage"] = "XXDEFAULT", ["ValidityStartDate"] = OData(referencia.AddDays(-1)), ["ValidityEndDate"] = OData(referencia.AddDays(1)) }]),
                    ["to_PhoneNumber"] = ColecaoSap([]),
                    ["to_EmailAddress"] = ColecaoSap([new Dictionary<string, object?> { ["EmailAddress"] = "cliente.pj.sintetico@example.invalid" }])
                }
            ])
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["d"] = d });
    }

    private static Dictionary<string, object?> ColecaoSap(IEnumerable<object> results)
    {
        return new Dictionary<string, object?> { ["results"] = results.ToArray() };
    }

    private static string OData(DateTimeOffset data)
    {
        return $"/Date({data.ToUnixTimeMilliseconds()})/";
    }
    private static ConsultaClienteSapAdapter CriarAdapterSapRaw(string payload)
    {
        var handler = new HandlerHttpSapFake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        }));
        var clienteHttp = new ClienteHttpParceiroNegocioSap(
            new HttpClient(handler),
            new OpcoesSap { BaseUrl = new UriBuilder("https", "example.invalid").Uri, Timeout = TimeSpan.FromSeconds(30) },
            new AutenticacaoSapNoOp());

        return new ConsultaClienteSapAdapter(clienteHttp, TimeProvider.System);
    }
    private static DocumentoFiscalNormalizado Documento(TipoDocumentoFiscal tipoDocumento, string documento)
    {
        return new DocumentoFiscalNormalizado(tipoDocumento, documento);
    }

    private static EnderecoParceiroNegocio Endereco(string uf)
    {
        return new EnderecoParceiroNegocio("END-SINTETICO-001", "Rua Sintetica", "Cidade Sintetica", uf, "CEP-SINTETICO", "BR");
    }

    private static ParceiroNegocioConsultado Parceiro(
        CategoriaParceiroNegocio categoria,
        int? businessPartnerCategory,
        IReadOnlyCollection<DocumentoFiscalNormalizado> documentos,
        EnderecoParceiroNegocio? endereco = null,
        IReadOnlyCollection<string>? emails = null)
    {
        return new ParceiroNegocioConsultado(
            businessPartnerCategory == 1 ? "BP-SINTETICO-001" : "BP-SINTETICO-002",
            businessPartnerCategory == 1 ? "Cliente PF Sintetico" : "Cliente PJ Sintetico",
            documentos,
            endereco,
            [],
            emails ?? [],
            SituacaoBloqueioSap.NaoBloqueado,
            categoria)
        {
            BusinessPartnerCategory = businessPartnerCategory,
            NomeCompletoParceiroNegocio = businessPartnerCategory == 1 ? "Cliente PF Sintetico" : "Cliente PJ Sintetico"
        };
    }

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);

        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class AutenticacaoSapNoOp : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class HandlerHttpSapFake : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public HandlerHttpSapFake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
    private sealed class ConsultaClienteSapGatewayFake : IConsultaClienteSapGateway
    {
        private readonly ResultadoConsultaSap<ParceiroNegocioConsultado> resultado;

        public ConsultaClienteSapGatewayFake(ResultadoConsultaSap<ParceiroNegocioConsultado> resultado)
        {
            this.resultado = resultado;
        }

        public string? BusinessPartnerRecebido { get; private set; }

        public Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken)
        {
            BusinessPartnerRecebido = businessPartner;
            return Task.FromResult(resultado);
        }
    }
}