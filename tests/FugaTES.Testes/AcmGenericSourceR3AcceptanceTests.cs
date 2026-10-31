using System.Net;
using System.Text;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.Integracao.Guardian;

namespace FugaTES.Testes;

public sealed class AcmGenericSourceR3AcceptanceTests
{
    private static ContratoSapToledoSnapshot Snapshot() => AcmGuardianReferenceMapperTests.Snapshot() with
    { NumeroContrato = "0000098765", DescricaoTipoContrato = "Compra soja", QuantidadeContrato = 125.5m,
        IdParceiro = "EM-9", Material = "MAT-9" };
    private static ContratoGuardianSet26 Desired() => new MapeadorContratoAcmGuardian(new(1.5m)).Mapear(Snapshot());

    [Fact]
    public void Generic_mapping_preserves_code_quantity_partner_material_and_approved_fields()
    {
        var result = Desired();
        Assert.Equal("0000098765", result.Codigo);
        Assert.Equal(125.5m, result.ValorContrato);
        Assert.Equal("EM-9", result.CodigoEmissor);
        Assert.Equal("MAT-9", result.CodigoItem);
        Assert.Equal("308102", result.Estado);
        Assert.Equal("309837", result.TipoLancamento);
        Assert.Equal(1.5m, result.ValorProximidadeFimContrato);
        Assert.Null(result.Veiculos);
        Assert.Equal("OC98765", MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot()).NumeroReferencia);
    }

    [Fact]
    public void New_H4_version_maps_the_same_Guardian_code()
    {
        var snapshot = Snapshot();
        var first = new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.JaExistente, snapshot.NumeroContrato,
            1, new string('A', 64), Guid.NewGuid(), snapshot);
        var mapper = new MapeadorContratoAcmGuardian(new(1.5m));
        Assert.Equal(mapper.Mapear(first), mapper.Mapear(first with { Versao = 7, IntegracaoId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Existing_identical_has_zero_writes()
    {
        var gateway = new Fake { Query = new(EstadoConsultaContratoGuardian.Existente, Desired()) };
        var result = await new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default);
        Assert.Equal(AcaoSincronizacaoContrato.NoAction, result.Acao);
        Assert.Equal(Desired().Codigo, gateway.Code);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task Existing_different_updates_once_and_preserves_type()
    {
        var gateway = new Fake { Query = new(EstadoConsultaContratoGuardian.Existente, Desired() with { ValorContrato = 100m }) };
        var marked = 0;
        var result = await new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default, () => marked++);
        Assert.Equal(AcaoSincronizacaoContrato.Update, result.Acao);
        Assert.Equal(1, gateway.Updates);
        Assert.Equal(0, gateway.Creates);
        Assert.Equal(1, marked);
        Assert.Equal(gateway.Query.Contrato!.TipoLancamento, gateway.Last!.TipoLancamento);
    }

    [Fact]
    public async Task Divergent_posting_type_blocks_update()
    {
        var gateway = new Fake { Query = new(EstadoConsultaContratoGuardian.Existente, Desired() with { TipoLancamento = "OTHER" }) };
        var error = await Assert.ThrowsAsync<DecisaoFuncionalPendenteSapException>(() =>
            new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default));
        Assert.Equal("ACM_TIPO_LANCAMENTO_IMMUTABLE", error.Decisao);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task Indeterminate_lookup_never_creates()
    {
        var gateway = new Fake();
        Assert.Equal(AcaoSincronizacaoContrato.Indeterminado,
            (await new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default)).Acao);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task Proven_absence_creates_once()
    {
        var gateway = new Fake { Query = new(EstadoConsultaContratoGuardian.InexistenteComprovado) };
        Assert.Equal(AcaoSincronizacaoContrato.Create,
            (await new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default)).Acao);
        Assert.Equal(1, gateway.Creates);
        Assert.Equal(0, gateway.Updates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Possible_write_timeout_is_indeterminate_without_retry(bool create)
    {
        var gateway = new Fake { Query = create ? new(EstadoConsultaContratoGuardian.InexistenteComprovado)
            : new(EstadoConsultaContratoGuardian.Existente, Desired() with { ValorContrato = 1m }), Timeout = true };
        var result = await new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default);
        Assert.Equal(AcaoSincronizacaoContrato.Indeterminado, result.Acao);
        Assert.Equal(1, gateway.Writes);
        Assert.Equal(1, gateway.Reads);
    }

    [Fact]
    public async Task Query_failure_does_not_mark_write_and_is_safe_to_retry()
    {
        var gateway = new Fake { ReadFailure = true };
        var marked = false;
        await Assert.ThrowsAsync<TimeoutException>(() => new SincronizarContratoGuardian(gateway)
            .ExecutarAsync(Desired(), default, () => marked = true));
        Assert.False(marked);
        Assert.Equal(0, gateway.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OTHER")]
    public async Task Unproven_write_identity_is_indeterminate(string? code)
    {
        var gateway = new Fake { Query = new(EstadoConsultaContratoGuardian.InexistenteComprovado), Unproven = true, ResponseCode = code };
        Assert.Equal(AcaoSincronizacaoContrato.Indeterminado,
            (await new SincronizarContratoGuardian(gateway).ExecutarAsync(Desired(), default)).Acao);
        Assert.Equal(1, gateway.Writes);
    }

    [Fact]
    public void Unspecified_vehicles_preserve_existing_associations()
    {
        var actual = Desired() with { Veiculos = [new("CAR1", "ABC1234")] };
        Assert.True(SincronizarContratoGuardian.Identicos(actual, Desired()));
        Assert.False(SincronizarContratoGuardian.Identicos(actual, Desired() with { Veiculos = [] }));
    }

    [Fact]
    public void Missing_carrier_blocks_before_external_request()
    {
        var requests = 0;
        void PrepareAndSend()
        {
            _ = MapeadorContratoAcmGuardian.MapearPreCadastro(Snapshot(), null, "https://callback.invalid");
            requests++;
        }
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(PrepareAndSend);
        Assert.Equal("ACM_CARRIER_SOURCE", error.Decisao);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void Proven_carrier_maps_only_approved_pre_registration_fields()
    {
        var pre = MapeadorContratoAcmGuardian.MapearPreCadastro(Snapshot(), new("CARRIER", "TRANSACTION-1"), "https://callback.invalid");
        Assert.Equal("FLX_RECEBIMENTO", pre.CodigoFluxo);
        Assert.Equal("MAT-9", pre.CodigoItem);
        Assert.Equal("EM-9", pre.CodigoEmissor);
        Assert.Equal(Snapshot().NumeroContrato, pre.CodigoContrato);
        Assert.Equal(1, pre.IdAgenda);
        Assert.Equal("OC98765", pre.NumeroReferencia);
        Assert.Equal(new[] { "AC3P", "3011", "10" }, pre.CamposAdicionais.Select(x => x.Valor));
        Assert.Equal(new[] { 1000, 1002, 1003 }, pre.CamposAdicionais.Select(x => x.Numero));
    }

    private const string Ns = "http://toledobrasil.com.br/WS_Guardian";
    private static string Envelope(string operation, string content) =>
        $"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><{operation}Response xmlns='{Ns}'>{content}</{operation}Response></s:Body></s:Envelope>";
    private static string Entry(bool enriched = false) =>
        "<VORetornoContrato><Erro>0</Erro><Descricao>OK</Descricao><Contrato>" +
        "<Codigo>0000098765</Codigo><Descricao>Compra soja</Descricao><ValorContrato>125.5</ValorContrato>" +
        "<Estado>308102</Estado><ValorProximidadeFimContrato>1.5</ValorProximidadeFimContrato><TipoLancamento>309837</TipoLancamento>" +
        "<CodigoEmissor>EM-9</CodigoEmissor><CodigoItem>MAT-9</CodigoItem>" +
        (enriched ? "<Sequencial>999</Sequencial><SaldoAtual>1</SaldoAtual><DataAlteracao>now</DataAlteracao><Extrato><x>anything</x></Extrato>" : "") +
        "</Contrato></VORetornoContrato>";
    private static GuardianContratoSoapClient Client(Handler handler) => new(new HttpClient(handler),
        new GuardianHttpOptions { ContratoSoapEndpoint = new Uri("https://guardian.invalid/contract"), Timeout = TimeSpan.FromSeconds(2) });

    [Fact]
    public async Task Query_wire_and_enriched_response_only_compare_controlled_fields()
    {
        var handler = new Handler(Envelope("ConsultaContrato", "<voRetornoContrato>" + Entry(true) + "</voRetornoContrato>"));
        var result = await new SincronizarContratoGuardian(Client(handler)).ExecutarAsync(Desired(), default);
        Assert.Equal(AcaoSincronizacaoContrato.NoAction, result.Acao);
        Assert.Equal(1, handler.Calls);
        Assert.Equal('"' + Ns + "/ConsultaContrato\"", handler.Action);
        XNamespace ns = Ns;
        var doc = XDocument.Parse(handler.Body!);
        Assert.Equal(Desired().Codigo, doc.Descendants(ns + "voDadosContratoCodigo").Single().Element(ns + "CodigoContrato")!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<voRetornoContrato />")]
    [InlineData("<voRetornoContrato><ArrayOfVORetornoContrato /></voRetornoContrato>")]
    [InlineData("<voRetornoContrato><VORetornoContrato><Erro>0</Erro></VORetornoContrato></voRetornoContrato>")]
    public async Task Empty_or_absent_response_has_zero_create(string body)
    {
        var handler = new Handler(Envelope("ConsultaContrato", body));
        var result = await new SincronizarContratoGuardian(Client(handler)).ExecutarAsync(Desired(), default);
        Assert.Equal(AcaoSincronizacaoContrato.Indeterminado, result.Acao);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("ConsultaContrato", handler.Action);
    }

    [Fact]
    public async Task Multiple_contracts_are_ambiguous_and_never_create()
    {
        var handler = new Handler(Envelope("ConsultaContrato", "<voRetornoContrato>" + Entry() + Entry() + "</voRetornoContrato>"));
        Assert.Equal(AcaoSincronizacaoContrato.Indeterminado,
            (await new SincronizarContratoGuardian(Client(handler)).ExecutarAsync(Desired(), default)).Acao);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_wire_omits_unspecified_vehicles_and_sends_explicit_empty(bool explicitEmpty)
    {
        var handler = new Handler(Envelope("AlteraContrato", "<voRetornoContrato>" + Entry() + "</voRetornoContrato>"));
        var desired = explicitEmpty ? Desired() with { Veiculos = [] } : Desired();
        var result = await Client(handler).AlterarComRespostaAsync(Desired() with { ValorContrato = 1m }, desired, default);
        Assert.Equal(desired.Codigo, result.CodigoContratoConfirmado);
        Assert.Equal('"' + Ns + "/AlteraContrato\"", handler.Action);
        XNamespace ns = Ns;
        var contract = XDocument.Parse(handler.Body!).Descendants(ns + "voDadosContrato").Single().Element(ns + "Contrato")!;
        Assert.Equal("309837", contract.Element(ns + "TipoLancamento")!.Value);
        Assert.Equal("125.5", contract.Element(ns + "ValorContrato")!.Value);
        Assert.Equal("EM-9", contract.Element(ns + "CodigoEmissor")!.Value);
        Assert.Equal("MAT-9", contract.Element(ns + "CodigoItem")!.Value);
        Assert.Equal(explicitEmpty, contract.Element(ns + "Veiculos") is not null);
    }

    [Fact]
    public async Task Update_client_rejects_type_change_before_request()
    {
        var handler = new Handler("");
        await Assert.ThrowsAsync<GuardianHttpException>(() => Client(handler)
            .AlterarComRespostaAsync(Desired(), Desired() with { TipoLancamento = "OTHER" }, default));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Fake : IGuardianContratoSincronizacaoGateway
    {
        public ConsultaContratoGuardian Query = new(EstadoConsultaContratoGuardian.Indeterminado);
        public int Creates, Updates, Reads;
        public int Writes => Creates + Updates;
        public string? Code, ResponseCode;
        public bool Timeout, ReadFailure, Unproven;
        public ContratoGuardianSet26? Last;
        public Task<ConsultaContratoGuardian> ConsultarAsync(string codigo, CancellationToken ct)
        { Reads++; Code = codigo; if (ReadFailure) throw new TimeoutException(); return Task.FromResult(Query); }
        public async Task CadastrarAsync(ContratoGuardianSet26 contrato, CancellationToken ct) => _ = await CadastrarComRespostaAsync(contrato, ct);
        public Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(ContratoGuardianSet26 contrato, CancellationToken ct)
        { Creates++; return Finish(contrato); }
        public Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(ContratoGuardianSet26 atual, ContratoGuardianSet26 desejado, CancellationToken ct)
        { Updates++; Assert.Equal(atual.TipoLancamento, desejado.TipoLancamento); return Finish(desejado); }
        private Task<ResultadoContratoGuardianComResposta> Finish(ContratoGuardianSet26 command)
        { Last = command; if (Timeout) throw new TimeoutException(); return Task.FromResult(new ResultadoContratoGuardianComResposta(Unproven ? ResponseCode : command.Codigo)); }
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public int Calls;
        public string? Body, Action;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Action = request.Headers.GetValues("SOAPAction").Single(); Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/xml") }; }
    }
}