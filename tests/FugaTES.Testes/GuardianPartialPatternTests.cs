using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.WinForms.Acm;
namespace FugaTES.Testes;
public sealed class GuardianPartialPatternTests
{
 [Fact]public async Task Partial_response_with_null_ticket_uses_returned_id_without_claiming_ticket_or_contract()
 {
  var h=new Handler();var http=new HttpClient(h);var o=new GuardianHttpOptions{BaseUrl=new Uri("https://guardian.invalid"),Timeout=TimeSpan.FromSeconds(5),PreCadastroEndpoint="/api/v1/Guardian/ProtocoloPreCadastro"};
  var client=new GuardianSet26HttpClient(http,o,new GuardianTokenClient(http,o,new Credentials()));
  var r=await client.EnviarAsync(new PreCadastroRecebimentoGuardianSet26(0,"FLX_RECEBIMENTO","SYNTHETIC-ITEM","SYNTHETIC-ISSUER","SYNTHETIC-CARRIER","SYNTHETIC-CONTRACT",1,"OC42","https://callback.invalid/",[new(1000,"AC3P"),new(1002,"3011"),new(1003,"10")]),default);
  Assert.Equal("2",r.Valor);Assert.Null(r.NumeroTicket);Assert.NotNull(r.DataCadastro);Assert.Equal(2,h.Calls);
 }
 [Fact]public void New_partial_dto_uses_exact_nested_wire_and_omits_unprovided_contract_fields()
 {
  var data=new GuardianProtocoloParcialDadosDto(3,"OC42","000001","SYNTHETIC-CARRIER",[new(1,"interno")]);
  var request=new GuardianProtocoloParcialRequestDto(new("https://callback.invalid/"),data);
  using var j=JsonDocument.Parse(JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
  var d=j.RootElement.GetProperty("dadosProtocoloPreCadastro");Assert.Equal(3,d.GetProperty("id").GetInt32());Assert.Equal("000001",d.GetProperty("codigoEmissor").GetString());Assert.Equal("SYNTHETIC-CARRIER",d.GetProperty("codigoTransportadora").GetString());
  Assert.Equal(5,d.EnumerateObject().Count());foreach(var name in new[]{"codigoFluxo","codigoItem","codigoContrato","idAgenda","versaoContrato","documentos","placaVeiculo","placaCarreta","identificador"})Assert.False(d.TryGetProperty(name,out _));
  Assert.Equal(1,d.GetProperty("camposAdicionais")[0].GetProperty("numero").GetInt32());Assert.Equal("interno",d.GetProperty("camposAdicionais")[0].GetProperty("valor").GetString());
 }
 [Fact]public void Missing_standard_carrier_must_be_visible_instead_of_silent_omission()
 {
  var m=AcmRestPreviewR53.Criar();Assert.Contains(m.Campos,x=>x.Campo.EndsWith("codigoTransportadora")&&x.Situacao.Contains("PADRÃO")&&x.Valor==AcmRestPreviewR53.Pending);
 }
 [Fact]public async Task Partial_preview_takes_configured_carrier_without_a_core_hardcode_and_never_dispatches()
 {
  var m=AcmRestPreviewR53.CriarParcial("CONFIGURED-CARRIER","https://callback.invalid/");using var j=JsonDocument.Parse(m.RascunhoJson);var d=j.RootElement.GetProperty("dadosProtocoloPreCadastro");
  Assert.Equal("CONFIGURED-CARRIER",d.GetProperty("codigoTransportadora").GetString());Assert.Equal(AcmRestPreviewR53.Pending,d.GetProperty("codigoEmissor").GetString());
  Assert.Equal("OC1031",d.GetProperty("numeroReferencia").GetString());Assert.DoesNotContain("10000",m.RascunhoJson);Assert.DoesNotContain("OC10\"",m.RascunhoJson);
  foreach(var field in new[]{"codigoContrato","codigoItem","codigoFluxo","idAgenda","placaVeiculo","identificador"})Assert.False(d.TryGetProperty(field,out _));
  var g=new ForbiddenGateway();Assert.False(m.ProntoParaEmissao);await Assert.ThrowsAsync<InvalidOperationException>(()=>m.EnviarAsync(g));Assert.Equal(0,g.Calls);
 }
 [Theory][InlineData(null)][InlineData("")][InlineData("NÃO VALIDADO")][InlineData("pendente")][InlineData("nao validado")][InlineData(" NÃO VALIDADO ")][InlineData("CARRIER-CODE-LONGER-THAN-TWENTY")]
 public void Partial_preview_missing_invalid_carrier_stays_blocked(string? code)
 {var m=AcmRestPreviewR53.CriarParcial(code,"https://callback.invalid/");using var j=JsonDocument.Parse(m.RascunhoJson);Assert.Equal(AcmRestPreviewR53.Pending,j.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("codigoTransportadora").GetString());Assert.False(m.ProntoParaEmissao);}
 [Fact]public void Partial_dto_accepts_supplied_synthetic_optional_fields_without_defaults()
 {
  var d=new GuardianProtocoloParcialDadosDto(3,"OC42","000001","SYNTHETIC-CARRIER",[new(1,"interno")]){PlacaVeiculo="SYNTHETIC-PLATE",PlacaCarreta="SYNTHETIC-TRAILER",Identificador="SYNTHETIC-TAG"};
  using var j=JsonDocument.Parse(JsonSerializer.Serialize(new GuardianProtocoloParcialRequestDto(new("https://callback.invalid/"),d),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
  Assert.Equal("SYNTHETIC-PLATE",j.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("placaVeiculo").GetString());Assert.Equal("SYNTHETIC-TAG",j.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("identificador").GetString());
 }
 sealed class ForbiddenGateway:IPreCadastroGuardianSet26Gateway{public int Calls;public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 p,CancellationToken ct){Calls++;throw new Exception("Unexpected write");}}
 sealed class Credentials:ICredenciaisGuardianSet26Provider{public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("synthetic","synthetic","synthetic"));}
 sealed class Handler:HttpMessageHandler{public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(r.RequestUri!.AbsolutePath.EndsWith("CreateToken")?"{\"token\":\"synthetic\"}":"{\"id\":2,\"codigoContrato\":null,\"codigoFluxo\":null,\"idAgenda\":null,\"numeroTicket\":null,\"dataCadastro\":\"2026-10-13T14:48:36.055989Z\"}",Encoding.UTF8,"application/json")});}}
}

