using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.CasosDeUso.SAP;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Testes;

public sealed class SapBusinessPartnerReadOnlyTests
{
    private static readonly DateTimeOffset Referencia = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CasoDeUsoCliente_ValidaEntradaEDelegaGateway()
    {
        var gateway = new GatewayFake(CategoriaParceiroNegocio.Cliente);
        var casoDeUso = new ConsultarClienteSapCasoDeUso(gateway);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.Equal("BP-SINTETICO-001", gateway.BusinessPartnerRecebido);
    }

    [Fact]
    public async Task CasoDeUsoFornecedor_ValidaEntradaEDelegaGateway()
    {
        var gateway = new GatewayFake(CategoriaParceiroNegocio.Fornecedor);
        var casoDeUso = new ConsultarFornecedorSapCasoDeUso(gateway);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
    }

    [Fact]
    public async Task CasoDeUsoTransportadora_ValidaEntradaEDelegaGateway()
    {
        var gateway = new GatewayFake(CategoriaParceiroNegocio.Transportadora);
        var casoDeUso = new ConsultarTransportadoraSapCasoDeUso(gateway);

        var resultado = await casoDeUso.ExecutarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
    }

    [Fact]
    public async Task CasoDeUso_RejeitaBusinessPartnerVazio()
    {
        var casoDeUso = new ConsultarClienteSapCasoDeUso(new GatewayFake(CategoriaParceiroNegocio.Cliente));

        await Assert.ThrowsAsync<ArgumentException>(() => casoDeUso.ExecutarAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task Cliente_Sucesso_ComToCustomerObjeto()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.NotNull(resultado.Dados);
        Assert.Equal(CategoriaParceiroNegocio.Cliente, resultado.Dados.CategoriaValidada);
    }

    [Fact]
    public async Task Cliente_404_RetornaNaoEncontrado()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.NotFound, string.Empty);

        Assert.Equal(StatusConsultaSap.NaoEncontrado, resultado.Status);
        Assert.Null(resultado.Dados);
    }

    [Fact]
    public async Task Cliente_ToCustomerNull_RetornaCategoriaNaoCorrespondente()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload());

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }

    [Fact]
    public async Task Cliente_ToCustomerResults_EFalhaContrato()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoComResults()));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }

    [Fact]
    public async Task Cliente_PayloadInvalido_EFalhaContrato()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, "{");

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }

    [Fact]
    public async Task Cliente_FalhaTecnica_ParaHttp500()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.InternalServerError, string.Empty);

        Assert.Equal(StatusConsultaSap.FalhaTecnica, resultado.Status);
    }

    [Fact]
    public async Task Fornecedor_Sucesso_ComToSupplierObjeto()
    {
        var resultado = await ExecutarFornecedorAsync(HttpStatusCode.OK, Payload(fornecedor: ObjetoFornecedor()));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.Equal(CategoriaParceiroNegocio.Fornecedor, resultado.Dados?.CategoriaValidada);
    }

    [Fact]
    public async Task Fornecedor_ToSupplierNull_RetornaCategoriaNaoCorrespondente()
    {
        var resultado = await ExecutarFornecedorAsync(HttpStatusCode.OK, Payload());

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }

    [Fact]
    public async Task Fornecedor_ToCustomerNaoProvaFornecedor()
    {
        var resultado = await ExecutarFornecedorAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()));

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }

    [Fact]
    public async Task Transportadora_Crm010Vigente_RetornaSucesso()
    {
        var roles = new[] { Role("CRM010", OData(Referencia.AddDays(-1)), OData(Referencia.AddDays(1))) };

        var resultado = await ExecutarTransportadoraAsync(HttpStatusCode.OK, Payload(roles: roles));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
    }

    [Fact]
    public async Task Transportadora_Crm010Ausente_RetornaCategoriaNaoCorrespondente()
    {
        var roles = new[] { Role("FLCU00", OData(Referencia.AddDays(-1)), OData(Referencia.AddDays(1))) };

        var resultado = await ExecutarTransportadoraAsync(HttpStatusCode.OK, Payload(roles: roles));

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }

    [Fact]
    public async Task Transportadora_ToCustomerSemCrm010_NaoProvaTransportadora()
    {
        var resultado = await ExecutarTransportadoraAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()));

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }
    [Fact]
    public async Task Transportadora_SupplierSemCrm010_NaoProvaTransportadora()
    {
        var resultado = await ExecutarTransportadoraAsync(HttpStatusCode.OK, Payload(fornecedor: ObjetoFornecedor()));

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }

    [Fact]
    public async Task Transportadora_Crm010Expirado_RetornaCategoriaNaoCorrespondente()
    {
        var roles = new[] { Role("CRM010", OData(Referencia.AddDays(-10)), OData(Referencia.AddDays(-1))) };

        var resultado = await ExecutarTransportadoraAsync(HttpStatusCode.OK, Payload(roles: roles));

        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, resultado.Status);
    }

    [Fact]
    public async Task Transportadora_Crm010ComDatasInvalidas_RetornaFalhaContrato()
    {
        var roles = new[] { Role("CRM010", "data-invalida", OData(Referencia.AddDays(1))) };

        var resultado = await ExecutarTransportadoraAsync(HttpStatusCode.OK, Payload(roles: roles));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }

    [Theory]
    [InlineData("BR1", TipoDocumentoFiscal.Cnpj)]
    [InlineData("BR2", TipoDocumentoFiscal.Cpf)]
    [InlineData("BR3", TipoDocumentoFiscal.InscricaoEstadual)]
    public async Task Fiscal_MapeiaTiposConhecidos(string tipoSap, TipoDocumentoFiscal tipoEsperado)
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), documentos: [Documento(tipoSap, "DOC-SINTETICO", null)]));

        Assert.Equal(tipoEsperado, resultado.Dados?.DocumentosFiscais.Single().Tipo);
    }

    [Fact]
    public async Task Fiscal_UsaShortQuandoLongAusente()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), documentos: [Documento("BR1", "CNPJ-ALFA-001", null)]));

        Assert.Equal("CNPJ-ALFA-001", resultado.Dados?.DocumentosFiscais.Single().Valor);
    }

    [Fact]
    public async Task Fiscal_UsaLongQuandoShortAusente()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), documentos: [Documento("BR2", null, "CPF-SINTETICO-001")]));

        Assert.Equal("CPF-SINTETICO-001", resultado.Dados?.DocumentosFiscais.Single().Valor);
    }

    [Fact]
    public async Task Fiscal_AceitaShortELongIguais()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), documentos: [Documento("BR3", "IE-SINTETICA", "IE-SINTETICA")]));

        Assert.Equal("IE-SINTETICA", resultado.Dados?.DocumentosFiscais.Single().Valor);
    }

    [Fact]
    public async Task Fiscal_ShortELongDiferentes_RetornaFalhaContrato()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), documentos: [Documento("BR1", "A", "B")]));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }

    [Fact]
    public async Task Fiscal_AmbosVazios_NaoFabricaValor()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), documentos: [Documento("BR1", " ", null)]));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.Empty(resultado.Dados!.DocumentosFiscais);
    }

    [Fact]
    public async Task Endereco_SemXxDefault_RetornaNulo()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), enderecos: [Endereco("2", "BILL_TO", true)]));

        Assert.Null(resultado.Dados?.EnderecoSelecionado);
    }

    [Fact]
    public async Task Endereco_UmXxDefaultVigente_SelecionaEndereco()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), enderecos: [Endereco("1", "XXDEFAULT", true)]));

        Assert.Equal("1", resultado.Dados?.EnderecoSelecionado?.AddressId);
    }

    [Fact]
    public async Task Endereco_MaisDeUmXxDefaultVigente_RetornaFalhaContrato()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), enderecos: [Endereco("2", "XXDEFAULT", true), Endereco("1", "XXDEFAULT", true)]));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }

    [Fact]
    public async Task Endereco_OrdemDaColecaoNaoDefineSelecao()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), enderecos: [Endereco("9", "BILL_TO", true), Endereco("3", "XXDEFAULT", true)]));

        Assert.Equal("3", resultado.Dados?.EnderecoSelecionado?.AddressId);
    }

    [Fact]
    public async Task Endereco_XxDefaultExpirado_NaoSelecionaOutroEndereco()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), enderecos: [Endereco("1", "XXDEFAULT", false), Endereco("2", "BILL_TO", true)]));

        Assert.Null(resultado.Dados?.EnderecoSelecionado);
    }

    [Fact]
    public async Task Endereco_XxDefaultComDatasInvalidas_RetornaFalhaContrato()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), enderecos: [Endereco("1", "XXDEFAULT", true, datasInvalidas: true)]));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }

    [Fact]
    public async Task Http_ExecutaSomenteGet()
    {
        var handler = new HandlerFake((request, _) => Task.FromResult(Resposta(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()))));
        var cliente = CriarClienteHttp(handler);
        var adapter = new ConsultaClienteSapAdapter(cliente, new TempoFake(Referencia));

        await adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.UltimaRequisicao?.Method);
    }

    [Fact]
    public async Task Http_IncluiSapClientQuandoConfigurado()
    {
        var handler = new HandlerFake((_, _) => Task.FromResult(Resposta(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()))));
        var cliente = CriarClienteHttp(handler, sapClient: "100");
        var adapter = new ConsultaClienteSapAdapter(cliente, new TempoFake(Referencia));

        await adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Contains("sap-client=100", handler.UltimaRequisicao!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_NaoIncluiSapClientQuandoAusente()
    {
        var handler = new HandlerFake((_, _) => Task.FromResult(Resposta(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()))));
        var cliente = CriarClienteHttp(handler);
        var adapter = new ConsultaClienteSapAdapter(cliente, new TempoFake(Referencia));

        await adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.DoesNotContain("sap-client", handler.UltimaRequisicao!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusConsultaSap.FalhaContrato)]
    [InlineData(HttpStatusCode.Unauthorized, StatusConsultaSap.FalhaAutenticacao)]
    [InlineData(HttpStatusCode.Forbidden, StatusConsultaSap.FalhaAutorizacao)]
    [InlineData(HttpStatusCode.NotFound, StatusConsultaSap.NaoEncontrado)]
    [InlineData(HttpStatusCode.InternalServerError, StatusConsultaSap.FalhaTecnica)]
    public async Task Http_MapeiaStatus(HttpStatusCode httpStatus, StatusConsultaSap statusEsperado)
    {
        var resultado = await ExecutarClienteAsync(httpStatus, Payload(cliente: ObjetoCliente()));

        Assert.Equal(statusEsperado, resultado.Status);
    }

    [Fact]
    public async Task Http_Timeout_RetornaFalhaTecnica()
    {
        var handler = new HandlerFake(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return Resposta(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()));
        });
        var cliente = CriarClienteHttp(handler, timeout: TimeSpan.FromMilliseconds(10));
        var adapter = new ConsultaClienteSapAdapter(cliente, new TempoFake(Referencia));

        var resultado = await adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.FalhaTecnica, resultado.Status);
    }

    [Fact]
    public async Task Http_TimeoutDuranteLeituraDoBody_RetornaFalhaTecnica()
    {
        var handler = new HandlerFake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ConteudoBodyLento(Payload(cliente: ObjetoCliente()), TimeSpan.FromSeconds(5))
        }));
        var cliente = CriarClienteHttp(handler, timeout: TimeSpan.FromMilliseconds(10));
        var adapter = new ConsultaClienteSapAdapter(cliente, new TempoFake(Referencia));

        var resultado = await adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.FalhaTecnica, resultado.Status);
    }

    [Fact]
    public async Task Http_CancelamentoDoCaller_EPropagado()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new HandlerFake(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return Resposta(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()));
        });
        var cliente = CriarClienteHttp(handler);
        var adapter = new ConsultaClienteSapAdapter(cliente, new TempoFake(Referencia));

        await Assert.ThrowsAsync<TaskCanceledException>(() => adapter.ConsultarAsync("BP-SINTETICO-001", cts.Token));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public async Task BusinessPartnerAusenteOuInvalido_RetornaFalhaContrato(string? businessPartner, bool incluirBusinessPartner)
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), businessPartner: businessPartner, incluirBusinessPartner: incluirBusinessPartner));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
        Assert.Null(resultado.Dados);
    }

    [Fact]
    public async Task ODataResults_ResultsVazio_EValido()
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente()));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
    }

    [Theory]
    [InlineData("to_BusinessPartnerRole", false, false)]
    [InlineData("to_BusinessPartnerRole", false, true)]
    [InlineData("to_BusinessPartnerTax", false, false)]
    [InlineData("to_BusinessPartnerTax", false, true)]
    [InlineData("to_BusinessPartnerAddress", false, false)]
    [InlineData("to_BusinessPartnerAddress", false, true)]
    [InlineData("to_AddressUsage", true, false)]
    [InlineData("to_AddressUsage", true, true)]
    [InlineData("to_PhoneNumber", true, false)]
    [InlineData("to_PhoneNumber", true, true)]
    [InlineData("to_EmailAddress", true, false)]
    [InlineData("to_EmailAddress", true, true)]
    public async Task ODataResults_ResultsAusenteOuNull_RetornaFalhaContrato(string colecao, bool colecaoDeEndereco, bool resultsNull)
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, PayloadComResultsInvalido(colecao, colecaoDeEndereco, resultsNull));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
        Assert.Null(resultado.Dados);
    }

    [Theory]
    [InlineData("to_BusinessPartnerRole", false)]
    [InlineData("to_BusinessPartnerTax", false)]
    [InlineData("to_BusinessPartnerAddress", false)]
    [InlineData("to_AddressUsage", true)]
    [InlineData("to_PhoneNumber", true)]
    [InlineData("to_EmailAddress", true)]
    public async Task ODataResults_ElementoNull_RetornaFalhaContratoSemExcecao(string colecao, bool colecaoDeEndereco)
    {
        // Retornar um ResultadoConsultaSap (em vez de lancar) ja prova ausencia de NullReferenceException.
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, PayloadComElementoNull(colecao, colecaoDeEndereco));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
        Assert.Null(resultado.Dados);
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("valor-desconhecido", true)]
    public async Task BloqueioSap_ValoresAusentesOuInvalidos_RetornamIndeterminado(string? bloqueioSap, bool incluirBloqueioSap)
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), bloqueioSap: bloqueioSap, incluirBloqueioSap: incluirBloqueioSap));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.Equal(SituacaoBloqueioSap.Indeterminado, resultado.Dados?.BloqueioSap);
    }

    [Theory]
    [InlineData("true", SituacaoBloqueioSap.Bloqueado)]
    [InlineData("X", SituacaoBloqueioSap.Bloqueado)]
    [InlineData("false", SituacaoBloqueioSap.NaoBloqueado)]
    public async Task BloqueioSap_ValoresReconhecidos_RetornamSituacaoCorrespondente(string bloqueioSap, SituacaoBloqueioSap situacaoEsperada)
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, Payload(cliente: ObjetoCliente(), bloqueioSap: bloqueioSap));

        Assert.Equal(StatusConsultaSap.Sucesso, resultado.Status);
        Assert.Equal(situacaoEsperada, resultado.Dados?.BloqueioSap);
    }
    [Theory]
    [InlineData("to_BusinessPartnerRole", false)]
    [InlineData("to_BusinessPartnerTax", false)]
    [InlineData("to_BusinessPartnerAddress", false)]
    [InlineData("to_AddressUsage", true)]
    [InlineData("to_PhoneNumber", true)]
    [InlineData("to_EmailAddress", true)]
    public async Task OData_ColecaoComNext_RetornaFalhaContrato(string colecao, bool colecaoDeEndereco)
    {
        var resultado = await ExecutarClienteAsync(HttpStatusCode.OK, PayloadComNext(colecao, colecaoDeEndereco));

        Assert.Equal(StatusConsultaSap.FalhaContrato, resultado.Status);
    }
    [Fact]
    public void Seguranca_CodigoProdutivo_NaoContemRegressoesDoIncremento001()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => arquivo.Contains("FugaTES.Integracao.SAP", StringComparison.OrdinalIgnoreCase) || arquivo.Contains("FugaTES.Aplicacao", StringComparison.OrdinalIgnoreCase));

        foreach (var arquivo in arquivos)
        {
            var source = File.ReadAllText(arquivo);
            if (Path.GetFileName(arquivo) == "AcmR104Bindings.cs")
            {
                const string approvedCallback = "http://192.168.17.40/api/guardian/set26/callback/recebimento";
                Assert.Equal(approvedCallback, FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.InternalHmlUrl);
                var binding = new FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackBinding("HML", approvedCallback,
                    "192.168.17.91/32", true, true, "SYNTHETIC_POLICY_TEST");
                Assert.False(FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.Validar(approvedCallback, true, null));
                Assert.False(FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.Validar(approvedCallback, false, binding));
                Assert.True(FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.Validar(approvedCallback, true, binding));
                Assert.False(FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.Validar(approvedCallback, true, binding with { Environment = "PRD" }));
                Assert.False(FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.Validar(approvedCallback, true, binding with { SourceAllowlistVerified = false }));
                Assert.False(FugaTES.Aplicacao.Servicos.Mvp.AcmR104CallbackPolicy.Validar(approvedCallback + "?other=1", true, binding));
                var literal = "\"" + approvedCallback + "\"";
                Assert.Single(Regex.Matches(source, Regex.Escape(literal)).Cast<Match>());
                source = source.Replace(literal, "\"https://example.invalid\"", StringComparison.Ordinal);
            }
            var achados = ScannerSegurancaCodigo.Detectar(source);
            if (Path.GetFileName(arquivo).Equals("PesagemSapHttpGateway.cs", StringComparison.OrdinalIgnoreCase))
                achados = achados.Where(achado => !achado.Equals("HttpMethod.Post", StringComparison.OrdinalIgnoreCase)).ToArray();
            // MVP SAP capabilities (Gate FUGATES-MVP-SAP-CAPABILITIES-SD-MM-ACM-R1): escrita SAP autorizada
            // (Outbound Delivery CREATE/PATCH, Material Document 101). Verbos de escrita liberados apenas
            // neste arquivo de adapters; nenhum endpoint operacional literal ou segredo e permitido.
            if (Path.GetFileName(arquivo).Equals("MvpSapEscritaAdapters.cs", StringComparison.OrdinalIgnoreCase))
                achados = achados.Where(achado =>
                    !achado.Equals("HttpMethod.Post", StringComparison.OrdinalIgnoreCase) &&
                    !achado.Equals("HttpMethod.Patch", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(achados.Count == 0, $"{arquivo}: {string.Join(", ", achados)}");
        }
    }

    [Fact]
    public void Scanner_DetectaEndpointLiteralComBarrasDentroDeString()
    {
        const string codigo = "var endpoint = \"https://servidor-real\";";

        Assert.NotEmpty(ScannerSegurancaCodigo.Detectar(codigo));
    }

    [Fact]
    public void Scanner_IgnoraComentarioComHttps()
    {
        const string codigo = "// exemplo https://servidor-real";

        Assert.Empty(ScannerSegurancaCodigo.Detectar(codigo));
    }

    [Fact]
    public void Scanner_DetectaConstrucaoGenericaDeMerge()
    {
        const string codigo = "var metodo = new HttpMethod(\"MERGE\");";

        Assert.Contains(ScannerSegurancaCodigo.Detectar(codigo), achado => achado.Contains("MERGE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scanner_NaoGeraFalsoPositivoParaExampleInvalidEmFixture()
    {
        const string codigo = "var uri = \"https://example.invalid\";";

        Assert.Empty(ScannerSegurancaCodigo.Detectar(codigo));
    }

    [Fact]
    public void Arquitetura_DtoSap_NaoVazaParaApplicationOuWinForms()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => arquivo.Contains("FugaTES.Aplicacao", StringComparison.OrdinalIgnoreCase) || arquivo.Contains("FugaTES.WinForms", StringComparison.OrdinalIgnoreCase));

        foreach (var arquivo in arquivos)
        {
            Assert.DoesNotContain("SapDto", File.ReadAllText(arquivo), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Arquitetura_WinFormsPermaneceSemHttpClient()
    {
        // Forms delegate to composed sessions; backend-command clients are separate runtime units.
        foreach (var form in typeof(FugaTES.WinForms.FrmMonitorMvp).Assembly.GetTypes()
            .Where(type => typeof(System.Windows.Forms.Form).IsAssignableFrom(type)))
        {
            ArchitectureBoundaries.NoHttpTransport(form);
        }
    }

    private static Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ExecutarClienteAsync(HttpStatusCode statusCode, string payload)
    {
        var handler = new HandlerFake((_, _) => Task.FromResult(Resposta(statusCode, payload)));
        var adapter = new ConsultaClienteSapAdapter(CriarClienteHttp(handler), new TempoFake(Referencia));
        return adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);
    }

    private static Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ExecutarFornecedorAsync(HttpStatusCode statusCode, string payload)
    {
        var handler = new HandlerFake((_, _) => Task.FromResult(Resposta(statusCode, payload)));
        var adapter = new ConsultaFornecedorSapAdapter(CriarClienteHttp(handler), new TempoFake(Referencia));
        return adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);
    }

    private static Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ExecutarTransportadoraAsync(HttpStatusCode statusCode, string payload)
    {
        var handler = new HandlerFake((_, _) => Task.FromResult(Resposta(statusCode, payload)));
        var adapter = new ConsultaTransportadoraSapAdapter(CriarClienteHttp(handler), new TempoFake(Referencia));
        return adapter.ConsultarAsync("BP-SINTETICO-001", CancellationToken.None);
    }

    private static ClienteHttpParceiroNegocioSap CriarClienteHttp(HandlerFake handler, string? sapClient = null, TimeSpan? timeout = null)
    {
        var httpClient = new HttpClient(handler);
        var opcoes = new OpcoesSap
        {
            BaseUrl = new UriBuilder("https", "example.invalid").Uri,
            SapClient = sapClient,
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };

        return new ClienteHttpParceiroNegocioSap(httpClient, opcoes, new AutenticacaoSapNoOp());
    }

    private static HttpResponseMessage Resposta(HttpStatusCode statusCode, string payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static string Payload(
        object? cliente = null,
        object? fornecedor = null,
        IEnumerable<object>? roles = null,
        IEnumerable<object>? documentos = null,
        IEnumerable<object>? enderecos = null,
        string? taxNext = null,
        string? businessPartner = "BP-SINTETICO-001",
        bool incluirBusinessPartner = true,
        string? bloqueioSap = "false",
        bool incluirBloqueioSap = true)
    {
        var d = new Dictionary<string, object?>
        {
            ["BusinessPartnerFullName"] = "Parceiro Sintetico",
            ["to_Customer"] = cliente,
            ["to_Supplier"] = fornecedor,
            ["to_BusinessPartnerRole"] = Colecao(roles),
            ["to_BusinessPartnerTax"] = Colecao(documentos, taxNext),
            ["to_BusinessPartnerAddress"] = Colecao(enderecos)
        };

        if (incluirBusinessPartner)
        {
            d["BusinessPartner"] = businessPartner;
        }

        if (incluirBloqueioSap)
        {
            d["BusinessPartnerIsBlocked"] = bloqueioSap;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["d"] = d });
    }

    private static string PayloadComResultsInvalido(string colecao, bool colecaoDeEndereco, bool resultsNull)
    {
        var root = JsonNode.Parse(Payload(cliente: ObjetoCliente(), enderecos: [Endereco("1", "BILL_TO", true)]))!.AsObject();
        var wrapper = ObterWrapperColecao(root, colecao, colecaoDeEndereco);

        if (resultsNull)
        {
            wrapper["results"] = null;
        }
        else
        {
            wrapper.Remove("results");
        }

        return root.ToJsonString();
    }

    private static string PayloadComElementoNull(string colecao, bool colecaoDeEndereco)
    {
        var root = JsonNode.Parse(Payload(cliente: ObjetoCliente(), enderecos: [Endereco("1", "BILL_TO", true)]))!.AsObject();
        var wrapper = ObterWrapperColecao(root, colecao, colecaoDeEndereco);
        wrapper["results"] = new JsonArray((JsonNode?)null);

        return root.ToJsonString();
    }

    private static string PayloadComNext(string colecao, bool colecaoDeEndereco)
    {
        var root = JsonNode.Parse(Payload(cliente: ObjetoCliente(), enderecos: [Endereco("1", "BILL_TO", true)]))!.AsObject();
        var wrapper = ObterWrapperColecao(root, colecao, colecaoDeEndereco);
        wrapper["__next"] = "continuacao-sintetica";

        return root.ToJsonString();
    }

    private static JsonObject ObterWrapperColecao(JsonObject root, string colecao, bool colecaoDeEndereco)
    {
        var d = root["d"]!.AsObject();
        if (!colecaoDeEndereco)
        {
            return d[colecao]!.AsObject();
        }

        var enderecos = d["to_BusinessPartnerAddress"]!.AsObject()["results"]!.AsArray();
        return enderecos[0]!.AsObject()[colecao]!.AsObject();
    }
    private static Dictionary<string, object?> Colecao(IEnumerable<object>? results, string? next = null)
    {
        var colecao = new Dictionary<string, object?> { ["results"] = results?.ToArray() ?? [] };
        if (next is not null)
        {
            colecao["__next"] = next;
        }

        return colecao;
    }

    private static object ObjetoCliente()
    {
        return new Dictionary<string, object?> { ["Customer"] = "CLIENTE-SINTETICO" };
    }

    private static object ObjetoFornecedor()
    {
        return new Dictionary<string, object?> { ["Supplier"] = "FORNECEDOR-SINTETICO" };
    }

    private static object ObjetoComResults()
    {
        return new Dictionary<string, object?> { ["results"] = Array.Empty<object>() };
    }

    private static object Role(string role, string inicio, string fim)
    {
        return new Dictionary<string, object?> { ["BusinessPartnerRole"] = role, ["ValidFrom"] = inicio, ["ValidTo"] = fim };
    }

    private static object Documento(string tipo, string? curto, string? longo)
    {
        return new Dictionary<string, object?> { ["BPTaxType"] = tipo, ["BPTaxNumber"] = curto, ["BPTaxLongNumber"] = longo };
    }

    private static object Endereco(string id, string uso, bool vigente, bool datasInvalidas = false)
    {
        var inicio = datasInvalidas ? "invalida" : OData(vigente ? Referencia.AddDays(-1) : Referencia.AddDays(-10));
        var fim = datasInvalidas ? "invalida" : OData(vigente ? Referencia.AddDays(1) : Referencia.AddDays(-1));
        return new Dictionary<string, object?>
        {
            ["AddressID"] = id,
            ["StreetName"] = "Rua Sintetica",
            ["CityName"] = "Cidade Sintetica",
            ["Region"] = "UF",
            ["PostalCode"] = "00000-000",
            ["Country"] = "BR",
            ["to_AddressUsage"] = Colecao([new Dictionary<string, object?> { ["AddressID"] = id, ["AddressUsage"] = uso, ["ValidityStartDate"] = inicio, ["ValidityEndDate"] = fim }]),
            ["to_PhoneNumber"] = Colecao([new Dictionary<string, object?> { ["PhoneNumber"] = "+550000000000" }]),
            ["to_EmailAddress"] = Colecao([new Dictionary<string, object?> { ["EmailAddress"] = "contato@example.invalid" }])
        };
    }

    private static string OData(DateTimeOffset data)
    {
        return $"/Date({data.ToUnixTimeMilliseconds()})/";
    }

    private static class ScannerSegurancaCodigo
    {
        private static readonly string[] PadroesLiterais =
        {
            "HttpMethod.Post",
            "HttpMethod.Put",
            "HttpMethod.Patch",
            "HttpMethod.Delete",
            "HttpMethod.Merge",
            "PostAsync",
            "PutAsync",
            "PatchAsync",
            "DeleteAsync",
            "new HttpClient",
            "Authorization =",
            "Authorization:",
            "Bearer ",
            "Basic ",
            "password =",
            "password:",
            "senha =",
            "senha:",
            "client_secret =",
            "client_secret:"
        };

        // URL operacional literal http(s):// exceto o dominio reservado de teste example.invalid.
        private static readonly Regex UrlOperacional = new(
            @"https?:\/\/(?!example\.invalid)[^\s""']+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Construcao generica de verbo mutavel, ex.: new HttpMethod(""MERGE"").
        private static readonly Regex VerboMutavelGenerico = new(
            @"new\s+HttpMethod\s*\(\s*""\s*(POST|PUT|PATCH|DELETE|MERGE)\s*""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static IReadOnlyList<string> Detectar(string codigo)
        {
            var limpo = RemoverComentarios(codigo);
            var achados = new List<string>();

            foreach (var padrao in PadroesLiterais)
            {
                if (limpo.Contains(padrao, StringComparison.OrdinalIgnoreCase))
                {
                    achados.Add(padrao);
                }
            }

            if (UrlOperacional.IsMatch(limpo))
            {
                achados.Add("url-operacional");
            }

            var verbo = VerboMutavelGenerico.Match(limpo);
            if (verbo.Success)
            {
                achados.Add("new HttpMethod(\"" + verbo.Groups[1].Value.ToUpperInvariant() + "\")");
            }

            return achados;
        }

        // Remove comentarios de linha e de bloco preservando literais de string e de char.
        // Um "//" dentro de "https://real" NAO e tratado como comentario.
        public static string RemoverComentarios(string conteudo)
        {
            var resultado = new StringBuilder(conteudo.Length);
            var emString = false;
            var emChar = false;
            var emComentarioLinha = false;
            var emComentarioBloco = false;

            for (var i = 0; i < conteudo.Length; i++)
            {
                var atual = conteudo[i];
                var proximo = i + 1 < conteudo.Length ? conteudo[i + 1] : '\0';

                if (emComentarioLinha)
                {
                    if (atual is '\n' or '\r')
                    {
                        emComentarioLinha = false;
                        resultado.Append(atual);
                    }

                    continue;
                }

                if (emComentarioBloco)
                {
                    if (atual == '*' && proximo == '/')
                    {
                        emComentarioBloco = false;
                        i++;
                    }

                    continue;
                }

                if (emString)
                {
                    resultado.Append(atual);
                    if (atual == '\\' && proximo != '\0')
                    {
                        resultado.Append(proximo);
                        i++;
                    }
                    else if (atual == '"')
                    {
                        emString = false;
                    }

                    continue;
                }

                if (emChar)
                {
                    resultado.Append(atual);
                    if (atual == '\\' && proximo != '\0')
                    {
                        resultado.Append(proximo);
                        i++;
                    }
                    else if (atual == '\'')
                    {
                        emChar = false;
                    }

                    continue;
                }

                if (atual == '/' && proximo == '/')
                {
                    emComentarioLinha = true;
                    i++;
                    continue;
                }

                if (atual == '/' && proximo == '*')
                {
                    emComentarioBloco = true;
                    i++;
                    continue;
                }

                if (atual == '"')
                {
                    emString = true;
                }
                else if (atual == '\'')
                {
                    emChar = true;
                }

                resultado.Append(atual);
            }

            return resultado.ToString();
        }
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

    private sealed class GatewayFake : IConsultaClienteSapGateway, IConsultaFornecedorSapGateway, IConsultaTransportadoraSapGateway
    {
        private readonly CategoriaParceiroNegocio categoria;

        public GatewayFake(CategoriaParceiroNegocio categoria)
        {
            this.categoria = categoria;
        }

        public string? BusinessPartnerRecebido { get; private set; }

        public Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken)
        {
            BusinessPartnerRecebido = businessPartner;
            var parceiro = new ParceiroNegocioConsultado(businessPartner, null, [], null, [], [], SituacaoBloqueioSap.NaoBloqueado, categoria);
            return Task.FromResult(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(parceiro));
        }
    }

    private sealed class AutenticacaoSapNoOp : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class HandlerFake : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public HandlerFake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public HttpRequestMessage? UltimaRequisicao { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaRequisicao = request;
            return responder(request, cancellationToken);
        }
    }

    private sealed class ConteudoBodyLento : HttpContent
    {
        private readonly string payload;
        private readonly TimeSpan atraso;

        public ConteudoBodyLento(string payload, TimeSpan atraso)
        {
            this.payload = payload;
            this.atraso = atraso;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await Task.Delay(atraso, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes, cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class TempoFake : TimeProvider
    {
        private readonly DateTimeOffset agora;

        public TempoFake(DateTimeOffset agora)
        {
            this.agora = agora;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return agora;
        }
    }
}
