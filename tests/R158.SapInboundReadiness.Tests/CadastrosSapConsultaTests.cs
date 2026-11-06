using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using FugaTES.Aplicacao.Consultas.CadastrosSap;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Cadastros;
namespace FugaTES.Testes;
public sealed class CadastrosSapConsultaTests
{
 const string Origin="https://vhfufds4ci.sap.fugacouros.com.br:44300";
 static OpcoesSap Options()=>new(){BaseUrl=new(Origin),SapClient="110",Timeout=TimeSpan.FromSeconds(5)};
 static object Product(string id="000000000007000001",string pt="MATERIAL SINTÉTICO",bool deleted=false)=>new{Product=id,ProductType="FERT",ProductGroup="TEST",BaseUnit="KG",CrossPlantStatus="04",IsMarkedForDeletion=deleted,to_Description=new{results=new[]{new{Product=id,Language="EN",ProductDescription="OTHER LANGUAGE"},new{Product=id,Language="PT",ProductDescription=pt}}}};
 static object Role(string id="123",string from="/Date(1577836800000)/",string to="/Date(253402300799000)/")=>new{BusinessPartner=id,BusinessPartnerRole="CRM010",ValidFrom=from,ValidTo=to};
 static object Bp(string id="123")=>new{BusinessPartner=id,BusinessPartnerFullName="TRANSPORTADORA SINTÉTICA",BusinessPartnerCategory="2",BusinessPartnerGrouping="TEST",BusinessPartnerIsBlocked=false,to_Customer=(object?)null,to_Supplier=(object?)null,to_BusinessPartnerRole=new{results=new[]{Role(id)}},to_BusinessPartnerTax=new{results=new[]{new{BPTaxType="BR1",BPTaxNumber="00000000000000"}}},to_BusinessPartnerAddress=new{results=Array.Empty<object>()}};
 static HttpResponseMessage Page(IEnumerable<object> rows,long? total=null,string? next=null)
 {var a=rows.ToArray();return Json(new{d=new Dictionary<string,object?>{{"results",a},{"__count",(total??a.Length).ToString()},{"__next",next}}});}
 static HttpResponseMessage Json(object data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data))};
 static Fake Material()=>new(u=>u.AbsolutePath.EndsWith("A_Product")?Page([Product()]):Json(new{d=new{Product="000000000007000001",ProductType="FERT",BaseUnit="KG",GrossWeight="2.500",to_Description=new{results=new[]{new{Language="PT",ProductDescription="SINTÉTICO"}}},to_Plant=new{results=new[]{new{Plant="3011"}}},to_ProductUnitsOfMeasure=new{results=new[]{new{AlternativeUnit="KG",QuantityNumerator="1"}}}}}));
 static Fake Carrier()=>new(u=>u.AbsolutePath.EndsWith("A_BusinessPartnerRole")?Page([Role()]):u.AbsolutePath.EndsWith("A_BusinessPartner")?Page([Bp()]):Json(new{d=Bp()}));
 [Fact] public async Task Material_preserves_key_PT_description_unit_and_deletion_status()
 {var h=Material();var auth=new Auth();var q=new MateriaisSapConsultaAdapter(new(h),Options(),auth);var p=await q.ConsultarAsync(new(),null,default);var x=Assert.Single(p.Registros);Assert.Equal("000000000007000001",x.Codigo);Assert.Equal("MATERIAL SINTÉTICO",x.Nome);Assert.Equal("FERT",x.Tipo);Assert.Contains("KG",x.Informacoes);Assert.Contains("04",x.Situacao);Assert.Equal(CategoriaCadastroSap.Materiais,q.Categoria);Assert.All(h.Calls,c=>Assert.Equal(HttpMethod.Get,c.Method));}
 [Fact] public async Task Name_filter_matches_the_complete_temporary_read_not_just_first_page()
 {var h=new Fake(u=>{var skip=int.Parse(QueryHelpers.ParseQuery(u.Query)["$skip"].ToString());return Page(Enumerable.Range(skip,skip==0?50:1).Select(i=>Product("P"+i.ToString("D3"),i==50?"ALVO":"OUTRO")),51);});var q=new MateriaisSapConsultaAdapter(new(h),Options(),new Auth());var p=await q.ConsultarAsync(new(Nome:"alvo"),null,default);Assert.Equal("P050",Assert.Single(p.Registros).Codigo);Assert.Equal(2,h.Calls.Count);Assert.Equal(1,p.Total);}
 [Fact] public async Task UI_pagination_uses_the_same_read_and_rejects_a_foreign_cursor_or_filter()
 {var h=new Fake(u=>{var skip=int.Parse(QueryHelpers.ParseQuery(u.Query)["$skip"].ToString());return Page(Enumerable.Range(skip,skip==0?50:1).Select(i=>Product("P"+i.ToString("D3"))),51);});var q=new MateriaisSapConsultaAdapter(new(h),Options(),new Auth());var first=await q.ConsultarAsync(new(),null,default);Assert.Equal(50,first.Registros.Count);Assert.NotNull(first.Continuacao);var last=await q.ConsultarAsync(new(),first.Continuacao,default);Assert.Single(last.Registros);Assert.Equal(2,h.Calls.Count);await Assert.ThrowsAsync<CadastroSapException>(()=>q.ConsultarAsync(new(Nome:"other"),first.Continuacao,default));await Assert.ThrowsAsync<CadastroSapException>(()=>q.ConsultarAsync(new(),"https://outside.invalid",default));Assert.Equal(2,h.Calls.Count);}
 [Fact] public async Task Missing_PT_description_is_not_replaced_by_an_arbitrary_other_language()
 {var h=new Fake(_=>Page([new{Product="P1",ProductType="FERT",BaseUnit="KG",ProductGroup="TEST",IsMarkedForDeletion=true,to_Description=new{results=new[]{new{Language="EN",ProductDescription="NOT PORTUGUESE"}}}}]));var row=Assert.Single((await new MateriaisSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default)).Registros);Assert.Empty(row.Nome);Assert.Contains("exclusão",row.Situacao);}
 [Fact] public async Task Failed_later_page_never_promotes_a_partial_catalogue()
 {var h=new Fake(u=>QueryHelpers.ParseQuery(u.Query)["$skip"].ToString()=="0"?Page(Enumerable.Range(0,50).Select(i=>Product("P"+i.ToString("D3"))),51):new(HttpStatusCode.InternalServerError){Content=new StringContent("private body")});var q=new MateriaisSapConsultaAdapter(new(h),Options(),new Auth());var e=await Assert.ThrowsAsync<CadastroSapException>(()=>q.ConsultarAsync(new(),null,default));Assert.DoesNotContain("private body",e.ToString());Assert.Equal(2,h.Calls.Count);}
 [Fact] public async Task Material_detail_keeps_original_units_plants_and_JSON_without_Guardian_mapping()
 {var h=Material();var q=new MateriaisSapConsultaAdapter(new(h),Options(),new Auth());var d=await q.ObterDetalheAsync("000000000007000001",default);Assert.Contains(d.Dados,x=>x.Valor=="3011");Assert.Contains(d.Dados,x=>x.Valor=="2.500");Assert.DoesNotContain("entidadeMaterial",d.Json);Assert.Contains("to_ProductUnitsOfMeasure",Uri.UnescapeDataString(h.Calls[0].Uri.Query));}
 [Fact] public async Task Carrier_is_selected_by_CRM010_even_without_customer_or_supplier()
 {var h=Carrier();var q=new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth());var x=Assert.Single((await q.ConsultarAsync(new(),null,default)).Registros);Assert.Equal("123",x.Codigo);Assert.Contains("CRM010",x.Situacao);Assert.Contains("vigente",x.Situacao.ToLowerInvariant());Assert.Equal(CategoriaCadastroSap.Transportadoras,q.Categoria);Assert.Contains("CRM010",Uri.UnescapeDataString(h.Calls[0].Uri.Query));}
 [Fact] public async Task No_CRM010_means_no_carrier_even_if_generic_BP_exists()
 {var h=new Fake(_=>Page([]));var p=await new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default);Assert.Empty(p.Registros);Assert.Single(h.Calls);}
 [Fact] public async Task Missing_joined_BP_is_a_failed_read_not_a_silent_carrier_omission()
 {var h=new Fake(u=>u.AbsolutePath.EndsWith("A_BusinessPartnerRole")?Page([Role()]):Page([]));await Assert.ThrowsAsync<CadastroSapException>(()=>new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default));}
 [Fact] public async Task Invalid_role_dates_remain_unconfirmed_and_do_not_grant_eligibility()
 {var h=new Fake(u=>u.AbsolutePath.EndsWith("A_BusinessPartnerRole")?Page([Role(from:"invalid")]):Page([Bp()]));var row=Assert.Single((await new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default)).Registros);Assert.Contains("não confirmada",row.Situacao.ToLowerInvariant());}
 [Fact] public async Task Carrier_detail_reads_fiscal_address_roles_and_contacts_from_SAP()
 {var h=Carrier();var d=await new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth()).ObterDetalheAsync("123",default);Assert.Contains(d.Dados,x=>x.Valor=="00000000000000");Assert.Contains(d.Dados,x=>x.Valor=="CRM010");Assert.Contains("to_BusinessPartnerAddress/to_EmailAddress",Uri.UnescapeDataString(h.Calls[0].Uri.Query));}
 [Theory][InlineData(401)][InlineData(403)]public async Task Authentication_errors_do_not_become_zero_rows(int status)
 {var h=new Fake(_=>new((HttpStatusCode)status){Content=new StringContent("not logged")});var ex=await Assert.ThrowsAsync<CadastroSapException>(()=>new MateriaisSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default));Assert.Equal(status,ex.HttpStatus);}
 [Theory][InlineData("https://outside.invalid")][InlineData("http://vhfufds4ci.sap.fugacouros.com.br:44300")]
 public void Wrong_origin_is_rejected_before_authentication(string origin)
 {var auth=new Auth();Assert.Throws<CadastroSapException>(()=>new MateriaisSapConsultaAdapter(new(Material()),new(){BaseUrl=new(origin),SapClient="110"},auth));Assert.Equal(0,auth.Calls);}
 [Fact] public async Task Cancellation_stops_before_any_request()
 {var h=Material();var auth=new Auth();using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new MateriaisSapConsultaAdapter(new(h),Options(),auth).ConsultarAsync(new(),null,ct.Token));Assert.Empty(h.Calls);Assert.Equal(0,auth.Calls);}
 [Fact] public async Task Untrusted_next_URL_is_never_followed_or_accepted_as_complete()
 {var h=new Fake(_=>Page([Product()],2,"https://outside.invalid/path?sap-client=110"));await Assert.ThrowsAsync<CadastroSapException>(()=>new MateriaisSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default));Assert.Single(h.Calls);}
 [Fact] public async Task Detail_identity_mismatch_is_rejected()
 {var h=new Fake(_=>Json(new{d=new{Product="WRONG"}}));await Assert.ThrowsAsync<CadastroSapException>(()=>new MateriaisSapConsultaAdapter(new(h),Options(),new Auth()).ObterDetalheAsync("P1",default));}
 [Fact] public async Task Text_product_key_is_escaped_as_one_literal_and_never_stripped()
 {var code="0MAT'A";var h=new Fake(_=>Page([Product(code)]));var row=Assert.Single((await new MateriaisSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(code),null,default)).Registros);Assert.Equal(code,row.Codigo);Assert.Contains("Product eq '0MAT''A'",Uri.UnescapeDataString(h.Calls[0].Uri.Query));}
 [Fact] public async Task Carrier_hydration_joins_every_batch_without_loss_or_extra_BP()
 {var ids=Enumerable.Range(0,21).Select(i=>"C"+i.ToString("D3")).ToArray();var batch=0;var h=new Fake(u=>u.AbsolutePath.EndsWith("A_BusinessPartnerRole")?Page(ids.Select(id=>Role(id))):Page((batch++==0?ids.Take(20):ids.Skip(20)).Select(id=>Bp(id))));var p=await new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default);Assert.Equal(21,p.Total);Assert.Equal(ids,p.Registros.Select(x=>x.Codigo));Assert.Equal(3,h.Calls.Count);Assert.All(h.Calls,x=>Assert.Equal(HttpMethod.Get,x.Method));}
 [Fact] public async Task Expired_CRM010_is_visible_with_expiry_and_never_called_current()
 {var h=new Fake(u=>u.AbsolutePath.EndsWith("A_BusinessPartnerRole")?Page([Role(from:"/Date(1262304000000)/",to:"/Date(1293840000000)/")]):Page([Bp()]));var row=Assert.Single((await new TransportadorasSapConsultaAdapter(new(h),Options(),new Auth()).ConsultarAsync(new(),null,default)).Registros);Assert.Contains("fora de vigência",row.Situacao);}
 [Fact] public async Task Failed_refresh_invalidates_the_previous_memory_cursor()
 {var failure=false;var h=new Fake(u=>failure?new(HttpStatusCode.Unauthorized){Content=new StringContent("not logged")}:Page(Enumerable.Range(int.Parse(QueryHelpers.ParseQuery(u.Query)["$skip"].ToString()),QueryHelpers.ParseQuery(u.Query)["$skip"].ToString()=="0"?50:1).Select(i=>Product("P"+i.ToString("D3"))),51));var q=new MateriaisSapConsultaAdapter(new(h),Options(),new Auth());var first=await q.ConsultarAsync(new(),null,default);failure=true;await Assert.ThrowsAsync<CadastroSapException>(()=>q.ConsultarAsync(new(),null,default));var calls=h.Calls.Count;await Assert.ThrowsAsync<CadastroSapException>(()=>q.ConsultarAsync(new(),first.Continuacao,default));Assert.Equal(calls,h.Calls.Count);}
 sealed class Auth:IAutenticacaoSap{public int Calls;public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.CompletedTask;}}
 sealed class Fake(Func<Uri,HttpResponseMessage> response):HttpMessageHandler{public List<(HttpMethod Method,Uri Uri)> Calls{get;}=[];protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls.Add((r.Method,r.RequestUri!));return Task.FromResult(response(r.RequestUri!));}}
}
