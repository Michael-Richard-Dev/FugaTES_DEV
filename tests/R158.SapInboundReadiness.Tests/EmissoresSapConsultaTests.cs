using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.EmissoresSap;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
namespace FugaTES.Testes;
public sealed class EmissoresSapConsultaTests
{
 const string Origin="https://vhfufds4ci.sap.fugacouros.com.br:44300";
 static object Row(string bp="123",bool cliente=true,bool fornecedor=false,string categoria="1",bool blocked=false)=>new{BusinessPartner=bp,BusinessPartnerFullName="EMISSOR SINTÉTICO",BusinessPartnerCategory=categoria,BusinessPartnerGrouping="TEST",BusinessPartnerIsBlocked=blocked,to_Customer=cliente?new{Customer=bp,CustomerAccountGroup="CUST"}:null,to_Supplier=fornecedor?new{Supplier=bp,SupplierAccountGroup="SUPL"}:null};
 static string Page(object[] rows,string? total=null,string? next=null)=>JsonSerializer.Serialize(new Dictionary<string,object?>{{"d",new Dictionary<string,object?>{{"results",rows},{"__count",total??rows.Length.ToString()},{"__next",next}}}});
 static (EmissoresSapConsultaAdapter Query,Fake Http,Auth Auth) Create(string body,HttpStatusCode status=HttpStatusCode.OK)
 {var h=new Fake(_=>new(status){Content=new StringContent(body)});var a=new Auth();return(new(new HttpClient(h),new(){BaseUrl=new(Origin+"/sap/opu/odata/sap/API_BUSINESS_PARTNER/"),SapClient="110",Timeout=TimeSpan.FromSeconds(5)},a),h,a);}
 [Fact] public async Task Both_roles_are_one_BP_and_no_property_group_is_invented()
 {var(q,h,a)=Create(Page([Row(cliente:true,fornecedor:true,categoria:"2")]));var p=await q.ConsultarAsync(new(),null,default);var x=Assert.Single(p.Registros);Assert.True(x.Cliente&&x.Fornecedor);Assert.Equal("PJ",x.Pessoa);Assert.Equal("Cliente e fornecedor",x.Funcoes);Assert.Equal("TEST",x.Grupo);Assert.Single(h.Requests);Assert.Equal(1,a.Calls);Assert.All(h.Requests,x=>Assert.Equal(HttpMethod.Get,x.Method));}
 [Theory][InlineData(true,false,"1","PF")][InlineData(false,true,"2","PJ")]
 public async Task Customer_and_supplier_classification_comes_from_their_navigation(bool c,bool s,string category,string label)
 {var(q,_,_)=Create(Page([Row(cliente:c,fornecedor:s,categoria:category,blocked:true)]));var x=Assert.Single((await q.ConsultarAsync(new(),null,default)).Registros);Assert.Equal(label,x.Pessoa);Assert.Equal(c,x.Cliente);Assert.Equal(s,x.Fornecedor);Assert.Equal("Bloqueado",x.Situacao);}
 [Fact] public async Task Query_contains_escaped_filters_and_fixed_SAP_scope()
 {var(q,h,_)=Create(Page([]));await q.ConsultarAsync(new("00123","D'ÁVILA"),null,default);var u=h.Requests[0].Uri;var query=Uri.UnescapeDataString(u.Query);Assert.Contains("sap-client=110",query);Assert.Contains("$inlinecount=allpages",query);Assert.Contains("$skip=0",query);Assert.Contains("$top=50",query);Assert.Contains("D''ÁVILA",query);Assert.Contains("BusinessPartner eq '00123'",query);Assert.Contains("to_Customer/Customer ne null",query);Assert.Contains("to_Supplier/Supplier ne null",query);}
 [Fact] public async Task Explicit_count_produces_next_offset_even_without_server_next()
 {var(q,h,_)=Create(Page([Row()],"51"));var p=await q.ConsultarAsync(new(),null,default);Assert.NotNull(p.Continuacao);Assert.Equal(51,p.Total);await q.ConsultarAsync(new(),p.Continuacao,default);Assert.Contains("$skip=1",Uri.UnescapeDataString(h.Requests[1].Uri.Query));}
 [Fact] public async Task Cursor_is_bound_to_filters_and_not_an_arbitrary_URL()
 {var(q,h,a)=Create(Page([Row()],"51"));var p=await q.ConsultarAsync(new(),null,default);await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(Nome:"outro"),p.Continuacao,default));await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),"https://outside.invalid/",default));Assert.Single(h.Requests);Assert.Equal(1,a.Calls);}
 [Theory]
 [InlineData("https://outside.invalid/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner?sap-client=110")]
 [InlineData(Origin+"/sap/opu/odata/sap/OTHER/A_BusinessPartner?sap-client=110")]
 [InlineData(Origin+"/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner?sap-client=100")]
 [InlineData(Origin+"/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner?sap-client=110&sap-client=100")]
 public async Task Untrusted_server_continuation_is_rejected_without_followup(string next)
 {var(q,h,a)=Create(Page([Row()],"51",next));await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),null,default));Assert.Single(h.Requests);Assert.Equal(1,a.Calls);}
 [Theory][InlineData(401,"SAP_UNAUTHORIZED")][InlineData(403,"SAP_FORBIDDEN")][InlineData(500,"SAP_HTTP_ERROR")]
 public async Task Http_failure_is_not_an_empty_list(int status,string code)
 {var(q,h,_)=Create("secret-looking response must not escape",(HttpStatusCode)status);var ex=await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),null,default));Assert.Equal(code,ex.Codigo);Assert.Equal(status,ex.HttpStatus);Assert.DoesNotContain("secret-looking",ex.ToString());Assert.Single(h.Requests);}
 [Fact] public async Task Empty_response_is_a_successful_empty_page()
 {var(q,_,_)=Create(Page([]));var p=await q.ConsultarAsync(new(),null,default);Assert.Empty(p.Registros);Assert.Equal(0,p.Total);Assert.Null(p.Continuacao);}
 [Theory][InlineData("{bad")][InlineData("{\"d\":{\"results\":[],\"__count\":\"4\"}}")][InlineData("{\"d\":{\"results\":[],\"results\":[]}}")]
 public async Task Invalid_or_incomplete_envelope_fails_closed(string body)
 {var(q,_,_)=Create(body);await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),null,default));}
 [Fact] public async Task BP_without_either_role_is_not_silently_called_an_emitter()
 {var(q,_,_)=Create(Page([Row(cliente:false,fornecedor:false)]));await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),null,default));}
 [Fact] public async Task Duplicate_rows_are_rejected_and_not_counted_twice()
 {var(q,_,_)=Create(Page([Row(),Row()]));await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),null,default));}
 [Fact] public async Task Cancellation_does_not_authenticate_or_send()
 {var(q,h,a)=Create(Page([]));using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>q.ConsultarAsync(new(),null,ct.Token));Assert.Empty(h.Requests);Assert.Equal(0,a.Calls);}
 [Theory][InlineData("12345678901","")][InlineData("","bad\nname")]
 public async Task Invalid_filters_are_rejected_before_authentication(string bp,string name)
 {var(q,h,a)=Create(Page([]));await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(bp,name),null,default));Assert.Empty(h.Requests);Assert.Equal(0,a.Calls);}
 [Fact] public async Task Detail_keeps_every_tax_address_and_contact_and_the_original_JSON()
 {
 var original=new{d=new{BusinessPartner="123",BusinessPartnerFullName="SINTÉTICO",BusinessPartnerCategory="1",to_Customer=new{Customer="123"},to_Supplier=(object?)null,to_BusinessPartnerTax=new{results=new[]{new{BPTaxType="BR2",BPTaxNumber="00000000000",BPTaxLongNumber=""},new{BPTaxType="BR3",BPTaxNumber="ISENTO",BPTaxLongNumber=""}}},to_BusinessPartnerAddress=new{results=new[]{new{AddressID="A1",StreetName="RUA SINTÉTICA",HouseNumber="1",AdditionalStreetPrefixName="COMPLEMENTO",CityName="CIDADE",Region="PR",PostalCode="00000000",Country="BR",to_AddressUsage=new{results=Array.Empty<object>()},to_PhoneNumber=new{results=new[]{new{PhoneNumber="000000000"}}},to_EmailAddress=new{results=new[]{new{EmailAddress="synthetic@example.invalid"}}}}}},to_BusinessPartnerRole=new{results=Array.Empty<object>()}}};
 var(q,h,_)=Create(JsonSerializer.Serialize(original));var d=await q.ObterDetalheAsync("123",default);Assert.Equal("123",d.Codigo);Assert.Contains(d.Dados,x=>x.Valor=="ISENTO");Assert.Contains(d.Dados,x=>x.Valor=="COMPLEMENTO");Assert.Contains(d.Dados,x=>x.Valor=="synthetic@example.invalid");Assert.Contains(d.Dados,x=>x.Valor=="PR");Assert.Contains("00000000000",d.Json);Assert.DoesNotContain("tipoEmissor",d.Json);Assert.Contains("to_BusinessPartnerAddress/to_EmailAddress",Uri.UnescapeDataString(h.Requests[0].Uri.Query));
 }
 [Fact] public async Task Detail_identity_mismatch_is_rejected()
 {var(q,_,_)=Create(JsonSerializer.Serialize(new{d=Row("456")}));await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ObterDetalheAsync("123",default));}
 [Fact] public async Task Detail_with_unfinished_navigation_is_rejected()
 {var(q,_,_)=Create("{\"d\":{\"BusinessPartner\":\"123\",\"to_Customer\":{\"Customer\":\"123\"},\"to_Supplier\":null,\"to_BusinessPartnerTax\":{\"results\":[],\"__next\":\"anything\"}}}");await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ObterDetalheAsync("123",default));}
 [Theory]
 [InlineData("http://vhfufds4ci.sap.fugacouros.com.br:44300","110")]
 [InlineData("https://outside.invalid","110")]
 [InlineData(Origin,"100")]
 public void Wrong_profile_is_rejected_before_authentication(string origin,string client)
 {var auth=new Auth();var h=new Fake(_=>throw new Exception("MUST_NOT_SEND"));Assert.Throws<EmissoresSapException>(()=>new EmissoresSapConsultaAdapter(new HttpClient(h),new(){BaseUrl=new(origin),SapClient=client,Timeout=TimeSpan.FromSeconds(5)},auth));Assert.Equal(0,auth.Calls);Assert.Empty(h.Requests);}
 [Fact] public async Task Oversized_body_is_rejected_without_leaking_the_remote_payload()
 {var(q,_,_)=Create(new string('x',1_048_577));var e=await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ConsultarAsync(new(),null,default));Assert.DoesNotContain(new string('x',100),e.ToString());}
 [Fact] public async Task A_valid_server_next_is_not_followed_and_filters_are_reconstructed_locally()
 {var(q,h,_)=Create(Page([Row()],"2",Origin+"/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner?sap-client=110&$skiptoken=untrusted-filter"));var p=await q.ConsultarAsync(new(),null,default);await q.ConsultarAsync(new(),p.Continuacao,default);Assert.DoesNotContain("skiptoken",h.Requests[1].Uri.Query);Assert.Contains("$filter",h.Requests[1].Uri.Query);}
 [Fact] public async Task Linked_customer_and_supplier_can_have_independent_numbering()
 {var row=new{BusinessPartner="123",BusinessPartnerFullName="SINTÉTICO",BusinessPartnerCategory="2",to_Customer=new{Customer="987"},to_Supplier=new{Supplier="456"}};var(q,_,_)=Create(Page([row]));var item=Assert.Single((await q.ConsultarAsync(new(),null,default)).Registros);Assert.Equal("123",item.Codigo);Assert.True(item.Cliente&&item.Fornecedor);}
 [Fact] public async Task Missing_expanded_detail_collection_is_not_silently_an_empty_collection()
 {var(q,_,_)=Create(JsonSerializer.Serialize(new{d=Row()}));var error=await Assert.ThrowsAsync<EmissoresSapException>(()=>q.ObterDetalheAsync("123",default));Assert.Equal("SAP_DETAIL_INCOMPLETE",error.Codigo);}
 [Fact] public async Task Alphanumeric_linked_accounts_are_preserved_instead_of_rejected_as_non_numeric()
 {var row=new{BusinessPartner="123",BusinessPartnerFullName="SINTÉTICO",BusinessPartnerCategory="2",to_Customer=new{Customer="CPD000001"},to_Supplier=new{Supplier="CPD000002"}};var(q,_,_)=Create(Page([row]));var x=Assert.Single((await q.ConsultarAsync(new(),null,default)).Registros);Assert.True(x.Cliente&&x.Fornecedor);}
 [Fact] public async Task Text_BP_identity_is_escaped_and_never_normalized_as_a_number()
 {var bp="0BP-A'1";var(q,h,_)=Create(Page([Row(bp)]));var row=Assert.Single((await q.ConsultarAsync(new(bp),null,default)).Registros);Assert.Equal(bp,row.Codigo);var filter=Uri.UnescapeDataString(h.Requests[0].Uri.Query);Assert.Contains("BusinessPartner eq '0BP-A''1'",filter);Assert.DoesNotContain("BusinessPartner eq 'BP-A''1'",filter);}
 [Fact] public async Task Quoted_text_BP_detail_is_escaped_as_one_OData_key()
 {var bp="0BP-A'1";var data=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Row(bp)))!.AsObject();foreach(var name in new[]{"to_BusinessPartnerTax","to_BusinessPartnerAddress","to_BusinessPartnerRole"})data[name]=new System.Text.Json.Nodes.JsonObject{{"results",new System.Text.Json.Nodes.JsonArray()}};var(q,h,_)=Create(new System.Text.Json.Nodes.JsonObject{{"d",data}}.ToJsonString());var value=await q.ObterDetalheAsync(bp,default);Assert.Equal(bp,value.Codigo);Assert.Equal(Origin,h.Requests[0].Uri.GetLeftPart(UriPartial.Authority));Assert.Contains("A_BusinessPartner('0BP-A''1')",Uri.UnescapeDataString(h.Requests[0].Uri.AbsolutePath));}
 [Fact] public async Task Other_SAP_person_categories_are_visible_as_their_code_not_misclassified_as_PF_PJ()
 {var(q,_,_)=Create(Page([Row(categoria:"3")]));var row=Assert.Single((await q.ConsultarAsync(new(),null,default)).Registros);Assert.Equal("Categoria SAP 3",row.Pessoa);}
 [Fact] public async Task Distinct_source_BP_keys_are_not_merged_by_leading_zero_equivalence()
 {var(q,_,_)=Create(Page([Row("00123"),Row("123")]));var rows=(await q.ConsultarAsync(new(),null,default)).Registros;Assert.Equal(new[]{"00123","123"},rows.Select(x=>x.Codigo));}
 sealed class Auth:IAutenticacaoSap{public int Calls;public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.CompletedTask;}}
 sealed class Fake(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
 {public List<(HttpMethod Method,Uri Uri)> Requests{get;}=[];protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){ct.ThrowIfCancellationRequested();Requests.Add((r.Method,r.RequestUri!));return Task.FromResult(respond(r));}}
}
