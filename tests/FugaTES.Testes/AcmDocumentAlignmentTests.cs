using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.WinForms.Acm;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.SAP;
namespace FugaTES.Testes;
[Collection("ACM_PREVIEW_MARKER")]
public sealed class AcmDocumentAlignmentTests
{
    [Fact] public void Documentary_mapping_closes_flow_center_and_reference_without_claiming_live_read()
    {
        var preview=AcmRestPreviewR53.Criar();using var json=JsonDocument.Parse(preview.RascunhoJson);
        var d=json.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal("FLX_RECEBIMENTO",d.GetProperty("codigoFluxo").GetString());
        Assert.Equal("OC1031",d.GetProperty("numeroReferencia").GetString());
        Assert.Equal("3011",d.GetProperty("camposAdicionais").EnumerateArray().Single(x=>x.GetProperty("numero").GetInt32()==1002).GetProperty("valor").GetString());
        Assert.Contains("NÃO CONSULTADO NESTA SESSÃO",preview.SapStatus);
        Assert.Contains(preview.Campos,x=>x.Campo.EndsWith("numeroReferencia")&&x.Situacao.Contains("NÃO É IDEMPOTÊNCIA"));
    }
    [Theory][InlineData("codigoItem")][InlineData("codigoEmissor")][InlineData("codigoContrato")]
    public void Unverified_guardian_identity_stays_pending(string field)
    {
        using var j=JsonDocument.Parse(AcmRestPreviewR53.Criar().RascunhoJson);
        Assert.Equal(AcmRestPreviewR53.Pending,j.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty(field).GetString());
    }
    [Fact]public async Task Carrier_standard_is_required_and_preview_never_dispatches()
    {
        var m=AcmRestPreviewR53.Criar();using var j=JsonDocument.Parse(m.RascunhoJson);
        var d=j.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal(AcmRestPreviewR53.Pending,d.GetProperty("codigoTransportadora").GetString());Assert.False(d.TryGetProperty("versaoContrato",out _));
        Assert.Contains(m.Campos,x=>x.Campo.EndsWith("codigoTransportadora")&&x.Situacao.Contains("PADRÃO"));
        Assert.Contains(m.Campos,x=>x.Campo=="sap.codigoEmissor"&&x.Valor=="0020000184");
        var g=new ForbiddenGateway();await Assert.ThrowsAsync<InvalidOperationException>(()=>m.EnviarAsync(g));Assert.Equal(0,g.Calls);Assert.False(m.ProntoParaEmissao);
    }
    [Theory]
    [InlineData("marker-item")][InlineData("marker-emitter")][InlineData("marker-carrier")][InlineData("marker-contract")][InlineData("marker-reference")][InlineData("marker-callback")][InlineData("marker-extra")]
    [InlineData("long-item")][InlineData("long-emitter")][InlineData("long-carrier")][InlineData("long-contract")][InlineData("long-extra")]
    [InlineData("duplicate-extra")][InlineData("unknown-document-key")][InlineData("missing-document-field")][InlineData("long-document")]
    public async Task Invalid_wire_is_rejected_before_any_authentication_or_business_http(string defect)
    {
        var p=Valid();p=defect switch
        {
            "marker-item"=>p with {CodigoItem="NÃO VALIDADO"},"marker-emitter"=>p with{CodigoEmissor="NÃO VALIDADO"},
            "marker-carrier"=>p with{CodigoTransportadora="NÃO VALIDADO"},"marker-contract"=>p with{CodigoContrato="NÃO VALIDADO"},
            "marker-reference"=>p with{NumeroReferencia="PENDENTE"},"marker-callback"=>p with{UrlParaRetorno="[URL_DE_RETORNO_A_VALIDAR]"},
            "marker-extra"=>p with{CamposAdicionais=[new(1000,"AC3P"),new(1002,"NÃO VALIDADO"),new(1003,"10")]},
            "long-item"=>p with{CodigoItem=new('X',31)},"long-emitter"=>p with{CodigoEmissor=new('X',21)},
            "long-carrier"=>p with{CodigoTransportadora=new('X',21)},"long-contract"=>p with{CodigoContrato=new('X',21)},
            "long-extra"=>p with{CamposAdicionais=[new(1000,new('X',51)),new(1002,"3011"),new(1003,"10")]},
            "duplicate-extra"=>p with{CamposAdicionais=[new(1000,"AC3P"),new(1002,"3011"),new(1002,"S033"),new(1003,"10")]},
            "unknown-document-key"=>p with{Documentos=[new Dictionary<string,string>{{"numero","DOC"}}]},
            "missing-document-field"=>p with{Documentos=[new Dictionary<string,string>{{"descricao","DOC"}}]},
            "long-document"=>p with{Documentos=[new Dictionary<string,string>{{"descricao",new('X',41)},{"pesoOrigem","10"}}]},_=>throw new Exception("Bad test")
        };
        var h=new Handler();var client=Client(h);
        await Assert.ThrowsAsync<GuardianHttpException>(()=>client.EnviarAsync(p,default));Assert.Equal(0,h.Calls);
    }
    [Fact] public async Task Valid_wire_omits_carrier_preserves_zeros_and_uses_nested_document_contract()
    {
        var h=new Handler();var p=Valid() with {CodigoTransportadora=null,TransportadoraPreenchidaNoGuardian=true,Documentos=[new Dictionary<string,string>{{"descricao","SYNTHETIC"},{"pesoOrigem","10.000"}}]};
        await Client(h).EnviarAsync(p,default);Assert.Equal(2,h.Calls);
        using var j=JsonDocument.Parse(h.Body!);var d=j.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal("000000000007000000",d.GetProperty("codigoItem").GetString());Assert.Equal("0020000184",d.GetProperty("codigoEmissor").GetString());
        Assert.False(d.TryGetProperty("transportadoraPreenchidaNoGuardian",out _));Assert.False(d.TryGetProperty("codigoTransportadora",out _));Assert.Equal("SYNTHETIC",d.GetProperty("documentos")[0].GetProperty("descricao").GetString());
        Assert.Equal("10.000",d.GetProperty("documentos")[0].GetProperty("pesoOrigem").GetString());
    }
    [Fact]public void Existing_mapper_requires_explicit_guardian_completion_decision()
    {
        var s=JsonSerializer.Deserialize<ContratoSapToledoSnapshot>("{\"NumeroContrato\":\"0000001031\",\"TipoContrato\":\"AC3P\",\"StatusContrato\":\"T\",\"OperacaoContrato\":\"10\",\"Centro\":\"3011\",\"Material\":\"000000000007000000\",\"IdParceiro\":\"0020000184\",\"CentroOpcional\":[]}")!;
        Assert.Throws<DecisaoFuncionalPendenteSapException>(()=>MapeadorContratoAcmGuardian.MapearPreCadastro(s,null,"https://callback.invalid/"));
        // An explicit decision is represented by the approved completion mode; it never uses IdParceiro as carrier.
        var p=MapeadorContratoAcmGuardian.MapearPreCadastro(s,null,"https://callback.invalid/",transportadoraPreenchidaNoGuardian:true);
        Assert.Null(p.CodigoTransportadora);Assert.Equal("0020000184",p.CodigoEmissor);Assert.Equal("OC1031",p.NumeroReferencia);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Null_carrier_requires_explicit_approved_acm_context(bool otherScenario)
    {
        var p=Valid() with {CodigoTransportadora=null};
        if(otherScenario)p=p with{TransportadoraPreenchidaNoGuardian=true,CamposAdicionais=[new(1000,"COMPRA"),new(1002,"CENTRO"),new(1003,"ENTRADA")]};
        var h=new Handler();await Assert.ThrowsAsync<GuardianHttpException>(()=>Client(h).EnviarAsync(p,default));Assert.Equal(0,h.Calls);
    }
    [Fact]public async Task Document_keys_must_match_json_case_even_with_ignore_case_dictionary()
    {
        var p=Valid() with{Documentos=[new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"Descricao","SYNTHETIC"},{"PesoOrigem","10"}}]};
        var h=new Handler();await Assert.ThrowsAsync<GuardianHttpException>(()=>Client(h).EnviarAsync(p,default));Assert.Equal(0,h.Calls);
    }
    [Fact]public void Actual_form_renders_approved_completion_mode_without_reads_or_writes()
    {
        Exception? error=null;var thread=new Thread(()=>{try{
            var repository=new ForbiddenRepository();using var form=new FugaTES.WinForms.FrmContratosAcm(new FugaTES.Aplicacao.Consultas.ContratosAcm.ContratosAcmConsulta(repository),restPreview:true);
            var buttons=Controls(form).OfType<System.Windows.Forms.Button>().ToArray();Assert.False(Assert.Single(buttons.Where(b=>b.Text.StartsWith("Enviar REST"))).Enabled);
            Assert.Single(buttons.Where(b=>b.Text=="Atualizar prévia")).PerformClick();Assert.Equal(0,repository.Calls);
            Assert.Contains(Controls(form).OfType<System.Windows.Forms.Label>(),l=>l.Text.Contains("Transportadora padrão provisória")&&l.Text.Contains("Envio REST: DESABILITADO"));
        }catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(5)));if(error is not null)throw error;
    }
    static IEnumerable<System.Windows.Forms.Control> Controls(System.Windows.Forms.Control c){foreach(System.Windows.Forms.Control child in c.Controls){yield return child;foreach(var item in Controls(child))yield return item;}}
    sealed class ForbiddenRepository:FugaTES.Aplicacao.Consultas.ContratosAcm.IContratosAcmReadRepository{public int Calls;public Task<IReadOnlyList<FugaTES.Aplicacao.Consultas.ContratosAcm.ContratoAcmLeitura>> ConsultarAsync(string c,CancellationToken ct){Calls++;throw new Exception("Unexpected database read");}}
    static PreCadastroRecebimentoGuardianSet26 Valid()=>new(0,"FLX_RECEBIMENTO","000000000007000000","0020000184","SYNTHETIC-CARRIER","0000001031",1,"OC1031","https://callback.invalid/",[new(1000,"AC3P"),new(1002,"3011"),new(1003,"10")]);
    static GuardianSet26HttpClient Client(Handler h){var o=new GuardianHttpOptions{BaseUrl=new Uri("https://guardian.invalid"),Timeout=TimeSpan.FromSeconds(5),PreCadastroEndpoint="/api/v1/Guardian/ProtocoloPreCadastro"};var http=new HttpClient(h);return new(http,o,new GuardianTokenClient(http,o,new Credentials()));}
    sealed class Credentials:ICredenciaisGuardianSet26Provider{public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("synthetic","synthetic","synthetic"));}
    sealed class Handler:HttpMessageHandler{public int Calls;public string? Body;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;var auth=r.RequestUri!.AbsolutePath.EndsWith("CreateToken");if(!auth)Body=await r.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent(auth?"{\"token\":\"synthetic\"}":"{\"id\":42,\"dataCadastro\":\"2026-10-13T12:00:00Z\"}",Encoding.UTF8,"application/json")};}}
    sealed class ForbiddenGateway:IPreCadastroGuardianSet26Gateway{public int Calls;public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 p,CancellationToken ct){Calls++;throw new Exception("Unexpected write");}}
}
