using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows.Forms;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.WinForms.Acm;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
namespace FugaTES.Testes;
[Collection("ACM_PREVIEW_MARKER")]
public sealed class AcmDualFlowR55Tests
{
 const string Ns="http://toledobrasil.com.br/WS_Guardian";
 [Fact]public async Task CadastraContrato_uses_config_and_voDados_nested_contract_with_exact_string_id()
 {
  var h=new SoapHandler();var client=new GuardianContratoSoapClient(new HttpClient(h),new(){ContratoSoapEndpoint=new Uri("https://guardian.invalid/WS.asmx"),Timeout=TimeSpan.FromSeconds(5)});
  await client.CadastrarComRespostaAsync(new("0000001031","SYNTHETIC CONTRACT",25.125m,"308102",0m,"309837"),default);
  var op=XDocument.Parse(h.Body!).Descendants(XName.Get("CadastraContrato",Ns)).Single();Assert.NotNull(op.Element(XName.Get("voConfiguracaoContrato",Ns)));
  var c=op.Element(XName.Get("voDadosContrato",Ns))?.Element(XName.Get("Contrato",Ns));Assert.NotNull(c);
  Assert.Equal("0000001031",c.Element(XName.Get("Codigo",Ns))!.Value);Assert.Equal("25.125",c.Element(XName.Get("ValorContrato",Ns))!.Value);Assert.Equal("0",c.Element(XName.Get("ValorProximidadeFimContrato",Ns))!.Value);
  Assert.Null(op.Element(XName.Get("Codigo",Ns)));Assert.Equal(1,h.Calls);
 }
 [Fact]public void Dual_preview_component_is_available_in_productive_winforms()
 {Assert.NotNull(typeof(FugaTES.WinForms.FrmContratosAcm).Assembly.GetType("FugaTES.WinForms.Acm.AcmDualPreviewR55"));}
 [Fact]public void Form_exposes_distinct_SAP_SOAP_REST_tabs_and_both_sends_disabled_without_SQL()
 {
  var marker=Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json");Assert.False(File.Exists(marker));File.WriteAllText(marker,"{\"CodigoTransportadoraCandidata\":\"SYNTHETIC-CARRIER\"}");
  try{Exception? error=null;var t=new Thread(()=>{try{
   var repo=new ForbiddenRepository();using var form=new FugaTES.WinForms.FrmContratosAcm(new ContratosAcmConsulta(repo),restPreview:true);
   var tabs=All(form).OfType<TabControl>().Single();Assert.Equal(new[]{"SAP / Estados","Contrato SOAP","Agendamento REST"},tabs.TabPages.Cast<TabPage>().Select(p=>p.Text));
   Assert.False(Assert.Single(All(form).OfType<Button>(),b=>b.Text.StartsWith("Enviar SOAP")).Enabled);Assert.False(Assert.Single(All(form).OfType<Button>(),b=>b.Text.StartsWith("Enviar REST")).Enabled);
   Assert.Single(All(form).OfType<Button>(),b=>b.Text=="Atualizar prévia").PerformClick();Assert.Equal(0,repo.Calls);
  }catch(Exception e){error=e;}});t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(TimeSpan.FromSeconds(5)));if(error is not null)throw error;
  }finally{File.Delete(marker);}
 }
 [Fact]public void Quantity_maps_to_SOAP_value_never_price_and_leading_zeros_remain()
 {
  var v=Version();var m=AcmDualPreviewR55.Criar(v,"SYNTHETIC_SOURCE_OFFLINE",Approval(),Links());Assert.True(m.SoapMapeado);Assert.True(m.RestMapeado);
  var doc=XDocument.Parse(m.SoapXml);Assert.Equal("25.125",doc.Descendants(XName.Get("ValorContrato",Ns)).Single().Value);Assert.Equal("0000001031",doc.Descendants(XName.Get("Codigo",Ns)).Single().Value);Assert.DoesNotContain("999.99",m.SoapXml);
  Assert.DoesNotContain("1031C",m.SoapXml);Assert.DoesNotContain("1031D",m.SoapXml);Assert.False(m.EnvioSoapPermitido);Assert.False(m.EnvioRestPermitido);
 }
 [Fact]public void Missing_quantity_is_not_replaced_by_price_zero_or_example_and_rest_link_stays_separate()
 {
  var v=Version();v=v with{Snapshot=v.Snapshot with{QuantidadeContrato=null}};var m=AcmDualPreviewR55.Criar(v,"SYNTHETIC_SOURCE_OFFLINE",Approval(),Links());Assert.False(m.SoapMapeado);
  var value=XDocument.Parse(m.SoapXml).Descendants(XName.Get("ValorContrato",Ns)).Single();Assert.Empty(value.Value);Assert.NotEmpty(value.Nodes().OfType<XComment>());
  using var j=JsonDocument.Parse(m.RestJson);Assert.Equal("0000001031",j.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("codigoContrato").GetString());
  Assert.True(m.RestMapeado);Assert.False(m.EnvioRestPermitido);
 }
 [Fact]public void Missing_functional_approval_never_uses_legacy_state_and_posting_type_defaults()
 {
  var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE");Assert.False(m.SoapMapeado);Assert.False(m.RestMapeado);
  var d=XDocument.Parse(m.SoapXml);foreach(var field in new[]{"Codigo","Estado","TipoLancamento"})Assert.Empty(d.Descendants(XName.Get(field,Ns)).Single().Value);
  Assert.DoesNotContain("308102",m.SoapXml);Assert.DoesNotContain("309837",m.SoapXml);
 }
 [Theory][InlineData("9999999999",2)][InlineData("0000001031",3)]
 public void Wrong_approval_contract_or_version_is_blocked(string number,int version)
 {var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",Approval()with{NumeroContratoSap=number,VersaoH4=version},Links());Assert.False(m.SoapMapeado);Assert.False(m.RestMapeado);}
 [Theory][InlineData("9999999999",2,"3011")][InlineData("0000001031",3,"3011")][InlineData("0000001031",2,"3008")]
 public void Wrong_SAP_scope_is_not_displayed_as_observed_and_does_not_map(string number,int version,string center)
 {var v=Version();v=v with{NumeroContrato=number,Versao=version,Snapshot=v.Snapshot with{NumeroContrato=number,Centro=center}};var m=AcmDualPreviewR55.Criar(v,"SYNTHETIC_SOURCE_OFFLINE",Approval(),Links());Assert.Contains("BLOQUEADO",m.SapEstado);Assert.False(m.SoapMapeado);Assert.False(m.RestMapeado);}
 [Fact]public void REST_complete_preview_contains_validated_contract_and_no_unprovided_optional_example_fields()
 {
  var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",Approval()with{CodigoGuardian="1031"},Links());using var j=JsonDocument.Parse(m.RestJson);
  Assert.Equal(2,j.RootElement.EnumerateObject().Count());var d=j.RootElement.GetProperty("dadosProtocoloPreCadastro");Assert.Equal("1031",d.GetProperty("codigoContrato").GetString());Assert.Equal("000000000007000000",d.GetProperty("codigoItem").GetString());Assert.Equal("0020000184",d.GetProperty("codigoEmissor").GetString());
  foreach(var field in new[]{"placaVeiculo","placaCarreta","codigoMotorista","documentos","versaoContrato"})Assert.False(d.TryGetProperty(field,out _));Assert.Equal("https://callback.invalid/operational",j.RootElement.GetProperty("configuracao").GetProperty("urlParaRetorno").GetString());
 }
 [Fact]public void Candidate_carrier_is_parameterized_but_not_automatically_approved_and_webhook_is_not_operational()
 {
  var m=AcmDualPreviewR55.Criar(transportadoraCandidata:"TESTAR4");Assert.False(m.RestMapeado);Assert.Contains(m.RestCampos,c=>c.Campo=="codigoTransportadora"&&c.Valor=="TESTAR4"&&c.Situacao.Contains("PENDENTES"));
  using var j=JsonDocument.Parse(m.RestJson);Assert.Equal(AcmRestPreviewR53.Pending,j.RootElement.GetProperty("configuracao").GetProperty("urlParaRetorno").GetString());Assert.Equal(AcmRestPreviewR53.Pending,j.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("codigoContrato").GetString());Assert.DoesNotContain("webhooksite.net",m.RestJson);
 }
 [Fact]public void SOAP_can_be_mapped_while_REST_correspondences_are_pending()
 {var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",Approval());Assert.True(m.SoapMapeado);Assert.False(m.RestMapeado);}
 [Fact]public async Task Both_execution_paths_are_blocked_before_gateways_even_when_offline_payloads_complete()
 {var g=new ForbiddenGateway();var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",Approval(),Links());await Assert.ThrowsAsync<InvalidOperationException>(()=>m.EnviarSoapAsync(g));await Assert.ThrowsAsync<InvalidOperationException>(()=>m.EnviarRestAsync(g));Assert.Equal(0,g.Calls);}
 [Fact]public async Task Invalid_SOAP_quantity_is_blocked_before_fake_HTTP()
 {var h=new SoapHandler();await Assert.ThrowsAsync<GuardianHttpException>(()=>new GuardianContratoSoapClient(new HttpClient(h),new(){ContratoSoapEndpoint=new Uri("https://guardian.invalid/WS.asmx"),Timeout=TimeSpan.FromSeconds(5)}).CadastrarAsync(new("0000001031","SYNTHETIC",-1m,"308102",0m,"309837"),default));Assert.Equal(0,h.Calls);}
 [Fact]public void Missing_SAP_reference_or_functional_provenance_blocks_offline_mapping()
 {Assert.False(AcmDualPreviewR55.Criar(Version(),null,Approval(),Links()).SoapMapeado);Assert.False(AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",Approval()with{FonteAprovacao=""},Links()).SoapMapeado);}
 [Fact]public void Cross_version_REST_links_do_not_transfer_approval()
 {var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",Approval(),Links()with{VersaoH4=3});Assert.True(m.SoapMapeado);Assert.False(m.RestMapeado);}
 [Theory][InlineData("PENDENTE")][InlineData("NÃO VALIDADO")][InlineData("NAO VALIDADO")]
 public void Pending_SAP_description_returns_blocked_draft_without_throwing(string marker)
 {var v=Version();v=v with{Snapshot=v.Snapshot with{DescricaoTipoContrato=marker}};var m=AcmDualPreviewR55.Criar(v,"SYNTHETIC_SOURCE_OFFLINE",Approval(),Links());Assert.False(m.SoapMapeado);Assert.Equal(AcmRestPreviewR53.Pending,m.SoapCampos.Single(f=>f.Campo=="Descricao").Valor);}
 [Theory][InlineData(true)][InlineData(false)]
 public void Unaccented_pending_functional_choices_return_blocked_draft(bool state)
 {var a=Approval();a=state?a with{Estado="NAO VALIDADO"}:a with{TipoLancamento="NAO VALIDADO"};Assert.False(AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",a,Links()).SoapMapeado);}
 [Theory][InlineData("item")][InlineData("emissor")][InlineData("carrier")][InlineData("contract")]
 public void REST_identifiers_exceeding_proven_schema_limits_are_not_declared_mapped(string field)
 {var a=Approval();var l=Links();switch(field){case "item":l=l with{CodigoItemGuardian=new('X',31)};break;case "emissor":l=l with{CodigoEmissorGuardian=new('X',21)};break;case "carrier":l=l with{CodigoTransportadoraGuardian=new('X',21)};break;case "contract":a=a with{CodigoGuardian=new('X',21)};break;}var m=AcmDualPreviewR55.Criar(Version(),"SYNTHETIC_SOURCE_OFFLINE",a,l);Assert.False(m.RestMapeado);Assert.False(m.EnvioRestPermitido);}
 static VersaoContratoSapToledo Version()
 {
  var s=JsonSerializer.Deserialize<ContratoSapToledoSnapshot>("{\"NumeroContrato\":\"0000001031\",\"TipoContrato\":\"AC3P\",\"DescricaoTipoContrato\":\"SYNTHETIC CONTRACT\",\"StatusContrato\":\"T\",\"QuantidadeContrato\":25.125,\"ValorUnitario\":999.99,\"DiasToler\":99,\"IdParceiro\":\"0020000184\",\"Material\":\"000000000007000000\",\"Centro\":\"3011\",\"OperacaoContrato\":\"10\",\"CentroOpcional\":[]}")!;
  return new(ResultadoRegistroVersaoContrato.JaExistente,s.NumeroContrato,2,new string('A',64),Guid.NewGuid(),s);
 }
 static DecisaoContratoAcmGuardianComprovada Approval()=>new("0000001031",2,"0000001031","308102","309837","SYNTHETIC_APPROVAL_OFFLINE");
 static VinculosRestAcmR55 Links()=>new("0000001031",2,"000000000007000000","0020000184","SYNTHETIC-CARRIER","https://callback.invalid/operational","SYNTHETIC_LINKS_OFFLINE");
 sealed class ForbiddenGateway:IGuardianContratoSincronizacaoGateway,IPreCadastroGuardianSet26Gateway
 {
  public int Calls;Exception Block(){Calls++;return new Exception("UNAUTHORIZED_GATEWAY_CALL");}
  public Task CadastrarAsync(ContratoGuardianSet26 c,CancellationToken t)=>throw Block();
  public Task<ResultadoContratoGuardianComResposta>CadastrarComRespostaAsync(ContratoGuardianSet26 c,CancellationToken t)=>throw Block();
  public Task<ResultadoContratoGuardianComResposta>AlterarComRespostaAsync(ContratoGuardianSet26 a,ContratoGuardianSet26 b,CancellationToken t)=>throw Block();
  public Task<ConsultaContratoGuardian>ConsultarAsync(string c,CancellationToken t)=>throw Block();
  public Task<ProtocoloGuardianSet26>EnviarAsync(PreCadastroRecebimentoGuardianSet26 p,CancellationToken t)=>throw Block();
 }
 static IEnumerable<Control> All(Control c){foreach(Control child in c.Controls){yield return child;foreach(var n in All(child))yield return n;}}
 sealed class ForbiddenRepository:IContratosAcmReadRepository{public int Calls;public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string c,CancellationToken ct){Calls++;throw new Exception("UNEXPECTED SQL");}}
 sealed class SoapHandler:HttpMessageHandler{public int Calls;public string? Body;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;Body=await r.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent("<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body><CadastraContratoResponse xmlns='"+Ns+"'><Erro>0</Erro><Contrato><Codigo>0000001031</Codigo></Contrato></CadastraContratoResponse></soap:Body></soap:Envelope>",Encoding.UTF8,"text/xml")};}}
}
