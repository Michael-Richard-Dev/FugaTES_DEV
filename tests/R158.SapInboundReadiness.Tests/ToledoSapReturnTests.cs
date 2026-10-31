using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapReturnTests
{
    static readonly Guid Receipt = Guid.Parse("12345678-1111-2222-3333-444444444444");
    const string Original = """{"SistemaBalanca":"Toledo","Centro":"3011","IdPesagem":"900001","TipoPesagem":"2","Material":"9000000","CampoFonte":"PRESERVAR","DataPesagem":"/Date(1767225600000)/","to_QualidPes":[{"Tipo":"IMPUREZA","ValorAferido":"1.1"}],"to_Pesagem":[{"Indice":1,"NrIndiceTotal":1,"ContratoACM":"0099999901","IdParceiro":"90000001","IdParceiroEntrega":"90000001","ParceiroEntregaNome":"SAP SYNTHETIC SUPPLIER"}]}""";
    static Task<bool> Allow(PlanoEnvioToledoSap plano,CancellationToken ct)=>Task.FromResult(true);
    static DocumentoToledoSap Doc() => ToledoSapPartnerFixture.Bind(new(Receipt,"3011","900001",Original,RegistrarPesagemToledoCasoDeUso.Fingerprint(Original),true,false));
    static ClaimIntegracaoMotor Claim() => new(Guid.NewGuid(),Guid.NewGuid(),$"toledo-sap:{Receipt:D}","GUARDIAN","SAP",ToledoSapWork.Operacao,Receipt.ToString("D"),ToledoSapWork.Fingerprint(Receipt,Doc().FingerprintOriginal),1,"OFFLINE_TEST",DateTimeOffset.UtcNow.AddMinutes(1));
    static DevolutivaToledoSapSnapshot Snap() => new(Receipt,Guid.NewGuid(),"FALHA_DEFINITIVA","FalhaAntesDoPost",503,1,false,false,true,false,true,new('A',64),new('B',64),DateTimeOffset.UtcNow);
    [Fact] public void Original_document_is_valid_without_inventing_TipoOperacao()
    { Assert.True(Doc().Valido()); Assert.DoesNotContain("TipoOperacao",Doc().DocumentoJson); }
    [Fact] public void Accepted_inbound_numeric_center_and_case_insensitive_identity_remain_valid_without_rewriting_JSON()
    {var json=Original.Replace("\"Centro\":\"3011\"","\"centro\":3011").Replace("SistemaBalanca","sistemabalanca").Replace("Toledo","TOLEDO").Replace("to_Pesagem","TO_PESAGEM");var doc=Doc() with{DocumentoJson=json,FingerprintOriginal=RegistrarPesagemToledoCasoDeUso.Fingerprint(json)};Assert.True(doc.Valido());Assert.Equal(json,doc.DocumentoJson);}
    [Theory][InlineData("{}")] [InlineData("invalid")]
    public void Invalid_or_altered_original_is_blocked(string json) => Assert.False((Doc() with {DocumentoJson=json}).Valido());
    [Fact] public void Receipt_identity_is_not_a_ticket_or_protocol_equivalence()
    { var c=ToledoSapWork.Candidato(Receipt,Doc().FingerprintOriginal);Assert.Equal(Receipt.ToString("D"),c.ChaveNegocio);Assert.Equal(ToledoSapWork.Operacao,c.Operacao);Assert.NotEqual(Doc().IdPesagem,c.ChaveNegocio); }
    [Fact] public async Task CSRF_and_POST_reuse_auth_and_send_proven_projection_without_new_business_fields()
    {
        var wire=new Wire();using var http=new HttpClient(wire);var gateway=new DocumentoToledoSapGateway(new(http,new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(3)},new Auth(),new CookieContainer()));
        var result=await gateway.EnviarOriginalAsync(Doc(),Allow,default);
        Assert.Equal(12,wire.Methods.Count);Assert.All(wire.Methods.Take(11),method=>Assert.Equal("GET",method));Assert.Equal("POST",wire.Methods.Last());Assert.Equal(ToledoSapODataPayload.Prepare(Original,new()).Json,wire.Body);Assert.DoesNotContain("TipoOperacao",wire.Body);Assert.True(wire.CsrfAndCookie);
        Assert.Equal(ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial,result.Codigo);Assert.True(result.Indeterminado);Assert.Equal(200,result.HttpStatus);
    }
    [Theory][InlineData(400,false)][InlineData(401,false)][InlineData(408,true)][InlineData(429,true)][InlineData(500,true)]
    public async Task HTTP_classification_never_blindly_retries_or_confirms_business_success(int code,bool unknown)
    {var wire=new Wire{PostCode=code};using var http=new HttpClient(wire);var result=await Gateway(http).EnviarOriginalAsync(Doc(),Allow,default);Assert.Equal(unknown,result.Indeterminado);Assert.Equal(1,wire.Posts);}
    [Fact] public async Task Revoked_fence_or_expired_window_after_CSRF_cannot_dispatch_POST()
    {var wire=new Wire();using var http=new HttpClient(wire);var result=await Gateway(http).EnviarOriginalAsync(Doc(),(_,_)=>Task.FromResult(false),default);Assert.False(result.PostIniciado);Assert.Equal(0,wire.Posts);Assert.Equal(11,wire.Methods.Count);Assert.All(wire.Methods,method=>Assert.Equal("GET",method));}
    [Fact] public async Task Failed_CSRF_cannot_dispatch_POST()
    {var wire=new Wire{FetchCode=503};using var http=new HttpClient(wire);var result=await Gateway(http).EnviarOriginalAsync(Doc(),Allow,default);Assert.Equal(ResultadoToledoSapCodigo.FalhaAntesDoPost,result.Codigo);Assert.False(result.PostIniciado);Assert.Equal(0,wire.Posts);}
    [Fact] public async Task Invalid_document_and_HTTP_endpoint_cannot_dispatch()
    {var wire=new Wire();using var http=new HttpClient(wire);Assert.False((await Gateway(http).EnviarOriginalAsync(Doc() with {Sintetico=true},Allow,default)).PostIniciado);Assert.False((await Gateway(http,"http://sap.invalid").EnviarOriginalAsync(Doc(),Allow,default)).PostIniciado);Assert.Empty(wire.Methods);}
    [Fact] public async Task Timeout_after_POST_is_unknown_and_not_retried()
    {var wire=new Wire{ThrowPost=true};using var http=new HttpClient(wire);var result=await Gateway(http).EnviarOriginalAsync(Doc(),Allow,default);Assert.Equal(ResultadoToledoSapCodigo.ResultadoIndeterminado,result.Codigo);Assert.Equal(1,wire.Posts);}
    [Fact] public async Task Motor_processor_records_send_intent_then_result_and_does_not_repeat()
    {var store=new Store();var gateway=new FakeGateway();var result=await new ProcessadorDocumentoToledoSap(store,gateway).ProcessarAsync(Claim(),default);Assert.Equal(new[]{"LOAD","INTENT","OUTCOME"},store.Events);Assert.Equal(1,gateway.Calls);Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva,result.Falha);}
    [Theory][InlineData("origin")][InlineData("synthetic")][InlineData("fingerprint")][InlineData("receipt")][InlineData("intent")][InlineData("operation")]
    public async Task Untrusted_or_stale_work_never_calls_SAP(string defect)
    {
        var store=new Store();var claim=Claim();if(defect=="origin")store.Document=Doc() with {OrigemComprovada=false};if(defect=="synthetic")store.Document=Doc() with {Sintetico=true};if(defect=="fingerprint")claim=claim with {FingerprintSha256=new('C',64)};if(defect=="receipt")store.Document=Doc() with {ReciboId=Guid.NewGuid()};if(defect=="intent")store.Intent=false;if(defect=="operation")claim=claim with {Operacao="OTHER"};
        var gateway=new FakeGateway();await new ProcessadorDocumentoToledoSap(store,gateway).ProcessarAsync(claim,default);Assert.Equal(0,gateway.Calls);
    }
    [Fact] public async Task Audit_failure_cannot_become_false_success()
    {var store=new Store{FailOutcome=true};await Assert.ThrowsAsync<InvalidOperationException>(()=>new ProcessadorDocumentoToledoSap(store,new FakeGateway()).ProcessarAsync(Claim(),default));}
    [Fact] public async Task Technical_ACK_remains_unknown_business_outcome_in_original_motor()
    {var result=await new ProcessadorDocumentoToledoSap(new Store(),new FakeGateway{Result=new(ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial,200,true)}).ProcessarAsync(Claim(),default);Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado,result.Falha);}
    [Fact] public void Known_pre_POST_failure_permits_only_authorized_manual_recovery()
    {Assert.True(DevolutivaToledoSapPolicy.Avaliar(Snap(),true).Permitido);Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap(),false).Permitido);}
    [Theory][InlineData("CONCLUIDA")][InlineData("PENDENTE")][InlineData("EM_PROCESSAMENTO")][InlineData("RESULTADO_INDETERMINADO")]
    public void Completed_active_and_unknown_states_block_retry(string state)=>Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with {Estado=state},true).Permitido);
    [Fact] public void HTTP400_without_correction_and_2xx_without_business_confirmation_block_retry()
    {Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with {Codigo="RejeicaoHttp",HttpStatus=400},true).Permitido);Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with {Codigo="AceiteHttpSemConfirmacaoEmpresarial",HttpStatus=200},true).Permitido);}
    [Fact] public void Missing_origin_history_or_live_binding_cannot_become_recoverable()
    {Assert.False(DevolutivaToledoSapPolicy.Avaliar(null,true).Permitido);Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with {OrigemComprovada=false},true).Permitido);Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with {Sintetico=true},true).Permitido);Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with {Revisao=""},true).Permitido);}
    [Fact] public async Task Confirmed_201_becomes_motor_success_only_after_outcome_persistence()
    {var gateway=new FakeGateway{Result=new(ResultadoToledoSapCodigo.RecebimentoConfirmadoSap,201,true){EntityId="https://sap.invalid/ReceberPesagem",RequestSha256=new('A',64),ResponseSha256=new('B',64),PayloadVersion="TOLEDO_SAP_ODATA_V1"}};var store=new Store();var result=await new ProcessadorDocumentoToledoSap(store,gateway).ProcessarAsync(Claim(),default);Assert.Null(result.Falha);Assert.Equal(new[]{"LOAD","INTENT","OUTCOME"},store.Events);store=new Store{FailOutcome=true};await Assert.ThrowsAsync<InvalidOperationException>(()=>new ProcessadorDocumentoToledoSap(store,gateway).ProcessarAsync(Claim(),default));}
    [Theory][InlineData("RecebimentoConfirmadoSap")][InlineData("RecebimentoJaConfirmadoSap")][InlineData("RegistroSapExistenteDivergente")]
    public void Proven_existing_or_confirmed_receipt_cannot_be_retried(string code)=>Assert.False(DevolutivaToledoSapPolicy.Avaliar(Snap() with{Codigo=code},true).Permitido);
    static DocumentoToledoSapGateway Gateway(HttpClient http,string url="https://sap.invalid")=>new(new(http,new(){BaseUrl=new(url),SapClient="110",Timeout=TimeSpan.FromSeconds(3)},new Auth(),new CookieContainer()));
    sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct){request.Headers.Authorization=new("Bearer","OFFLINE_SYNTHETIC");return Task.CompletedTask;}}
    sealed class Wire:HttpMessageHandler
    {
        public int PostCode=200,FetchCode=200,Posts;public bool ThrowPost,CsrfAndCookie;public string Body="";public List<string> Methods=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {Methods.Add(r.Method.Method);Assert.Equal("sap.invalid",r.RequestUri!.Host);Assert.Contains("sap-client=110",r.RequestUri.Query);Assert.NotNull(r.Headers.Authorization);
         if(r.RequestUri.Query.Contains("$inlinecount="))return new(HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")};
         if(r.Method==HttpMethod.Get&&!r.Headers.Contains("X-CSRF-Token"))return new(HttpStatusCode.NotFound){Content=new StringContent("""{"error":{"code":"NOT_FOUND","message":{"value":"Not found"}}}""")};
         if(r.Method==HttpMethod.Get){var response=new HttpResponseMessage((HttpStatusCode)FetchCode);response.Headers.Add("X-CSRF-Token","OFFLINE_CSRF");response.Headers.Add("Set-Cookie","TEST=OFFLINE; Path=/");return response;}
         Posts++;Body=await r.Content!.ReadAsStringAsync(ct);CsrfAndCookie=r.Headers.GetValues("X-CSRF-Token").Single()=="OFFLINE_CSRF"&&r.Headers.GetValues("Cookie").Single().Contains("TEST=OFFLINE");if(ThrowPost)throw new TaskCanceledException();return new((HttpStatusCode)PostCode){Content=new StringContent("{}")};}
    }
    sealed class Store:IDevolutivaToledoSapStore
    {
        public DocumentoToledoSap Document=Doc();public bool Intent=true,FailOutcome;public List<string> Events=[];
        public Task<DocumentoToledoSap?> ObterAsync(ClaimIntegracaoMotor c,CancellationToken ct){Events.Add("LOAD");return Task.FromResult<DocumentoToledoSap?>(Document);}
        public Task<bool> ValidarDespachoAsync(ClaimIntegracaoMotor c,PlanoEnvioToledoSap plano,CancellationToken ct)=>Task.FromResult(Intent);
        public Task<bool> MarcarDespachoAsync(ClaimIntegracaoMotor c,CancellationToken ct){Events.Add("INTENT");return Task.FromResult(Intent);}
        public Task RegistrarRetornoAsync(ClaimIntegracaoMotor c,RetornoToledoSap r,CancellationToken ct){Events.Add("OUTCOME");if(FailOutcome)throw new InvalidOperationException("OFFLINE_AUDIT_FAILURE");return Task.CompletedTask;}
    }
    sealed class FakeGateway:IDocumentoToledoSapGateway{public int Calls;public RetornoToledoSap Result=new(ResultadoToledoSapCodigo.FalhaAntesDoPost,503,false);public async Task<RetornoToledoSap> EnviarOriginalAsync(DocumentoToledoSap d,Func<PlanoEnvioToledoSap,CancellationToken,Task<bool>> gate,CancellationToken ct){if(!await gate(new(new('A',64),"TOLEDO_SAP_ODATA_V1",1767225600000),ct))return new(ResultadoToledoSapCodigo.FalhaAntesDoPost,null,false);Calls++;return Result;}}
}
