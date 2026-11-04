using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace FugaTES.Testes;

public sealed class ToledoReceiptTests
{
    private static JsonObject Payload()=>JsonNode.Parse(ToledoCallbackTests.ValidJson)!.AsObject();
    private static ToledoPesagemCallbackDto Parse(JsonObject json)
    {using var doc=JsonDocument.Parse(json.ToJsonString());return ToledoPesagemCallbackDto.Deserializar(doc.RootElement);}
    private static ServiceProvider Services(Store store)
    {var services=new ServiceCollection();services.AddLogging();services.AddSingleton<IRegistroPesagemToledoStore>(store);services.AddScoped<RegistrarPesagemToledoCasoDeUso>();services.AddSingleton<IToledoCallbackAdmission,OfflineToledoAdmission>();return services.BuildServiceProvider();}

    [Fact]
    public void Full_payload_preserves_identifiers_weights_quality_and_unspecified_local_time()
    {
        var dto=Parse(Payload());
        Assert.Equal("0020318",dto.IdPesagem);Assert.Equal("0000001155",dto.Pesagens[0].ContratoACM);
        Assert.Equal("0020000051",dto.Pesagens[0].IdParceiro);Assert.Equal("7000000",dto.Material);
        Assert.Equal(new DateTime(2026,10,27,12,0,22),dto.DataPesagemLocal);Assert.Equal(DateTimeKind.Unspecified,dto.DataPesagemLocal.Kind);
        Assert.Equal(40000m,dto.PesoBruto);Assert.Equal(20000m,dto.PesoTara);Assert.Equal(20000m,dto.PesoAposDesconto);
        Assert.Equal(8,dto.Qualidades.Count);Assert.Equal("",dto.Qualidades[1].Tipo);Assert.Equal("",dto.Qualidades[1].ValorAferido);
        Assert.Equal("2",dto.TipoPesagem);Assert.DoesNotContain("EstadoCodigo",dto.DocumentoJson);
        Assert.DoesNotContain("ProtocoloGuardian",dto.DocumentoJson);Assert.DoesNotContain("IntegrationId",dto.DocumentoJson);
    }
    [Theory]
    [InlineData("pt-BR")][InlineData("en-US")][InlineData("de-DE")]
    public void Parsing_is_independent_of_machine_culture(string culture)
    {
        var previous=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);Assert.Equal(20000m,Parse(Payload()).PesoAntesDesconto);}
        finally{CultureInfo.CurrentCulture=previous;}
    }
    [Theory]
    [InlineData("20318")][InlineData("900719925474099312345678901234567890")]
    public void Integer_json_id_is_read_as_exact_digits_without_double_or_int64(string id)
    {
        var p=Payload();p["IdPesagem"]=JsonNode.Parse(id);Assert.Equal(id,Parse(p).IdPesagem);
    }
    [Theory]
    [InlineData("SistemaBalanca")][InlineData("DataPesagem")][InlineData("TipoPesagem")][InlineData("IdPesagem")]
    [InlineData("Centro")][InlineData("Material")][InlineData("PesoBruto")][InlineData("PesoTara")]
    [InlineData("PesoAntesDesconto")][InlineData("PesoAposDesconto")][InlineData("to_Pesagem")][InlineData("to_QualidPes")]
    public async Task Missing_required_field_is_rejected_before_store(string field)
    {
        var p=Payload();p.Remove(field);var store=new Store();using var services=Services(store);
        var result=await ToledoCallbackTests.Post(p.ToJsonString(),services);
        Assert.Equal(400,result.Status);Assert.Equal(0,store.Calls);
    }
    [Theory]
    [InlineData("DataPesagem","02/30/2026 12:00:22 PM")]
    [InlineData("DataPesagem","10/27/2026 12:00:22 PMZ")]
    [InlineData("DataPesagem","2026-11-03 09:23:54")][InlineData("DataPesagem","13/13/2026 10:00:00")]
    [InlineData("PesoTara","-1")][InlineData("PesoAntesDesconto","1e3")]
    [InlineData("PesoAposDesconto","0.12345678901234567890123456789")]
    [InlineData("IdPesagem","1.5")][InlineData("IdPesagem","")][InlineData("Material","7e6")]
    [InlineData("SistemaBalanca","Other")]
    public async Task Invalid_values_are_rejected_without_rounding_or_disclosing_data(string field,string value)
    {
        var p=Payload();p[field]=value;var store=new Store();using var services=Services(store);
        var result=await ToledoCallbackTests.Post(p.ToJsonString(),services);
        Assert.Equal(400,result.Status);Assert.Contains("INVALID_TOLEDO_CALLBACK",result.Body);Assert.Equal(0,store.Calls);
    }
    // O Guardian manda a data em DUAS convencoes, conforme o caminho, e a virgula decimal vem da
    // propria balanca. Recusar qualquer uma delas derruba pesagem boa: foi o que aconteceu com a
    // 0020345, de 3 de novembro de 2026, recusada porque o dia veio como "3" e a lista de
    // formatos so aceitava "dd". Estes casos sao os que TEM de passar.
    [Theory]
    [InlineData("DataPesagem","11/3/2026 9:23:54 AM")]    // automatico: mes/dia, sem zero, AM/PM
    [InlineData("DataPesagem","11/03/2026 09:23:54 AM")]
    [InlineData("DataPesagem","03/11/2026 08:30:44")]     // manual: dia/mes, 24 horas
    [InlineData("DataPesagem","3/11/2026 8:30:44")]
    [InlineData("PesoBruto","5000,0")]                    // virgula decimal da balanca
    [InlineData("PesoBruto","5000.0")]
    public async Task Accepted_guardian_formats_are_not_rejected(string field,string value)
    {
        var p=Payload();p[field]=value;var store=new Store();using var services=Services(store);
        var result=await ToledoCallbackTests.Post(p.ToJsonString(),services);
        Assert.Equal(200,result.Status);Assert.DoesNotContain("INVALID_TOLEDO_CALLBACK",result.Body);
    }
    [Theory]
    [InlineData(0)][InlineData(1)]
    public void Multiple_items_validate_both_explicit_contiguous_conventions_without_reweighting(int first)
    {
        var p=Payload();var items=p["to_Pesagem"]!.AsArray();var second=items[0]!.DeepClone();items.Add(second);
        for(var i=0;i<2;i++){items[i]!["Indice"]=(i+first).ToString();items[i]!["NrIndiceTotal"]="2";items[i]!["PercentualDesdobro"]="50";}
        var dto=Parse(p);Assert.Equal(2,dto.Pesagens.Count);Assert.Equal(first,dto.Pesagens[0].Indice);
        Assert.Equal(20000m,dto.PesoAntesDesconto);Assert.All(dto.Pesagens,x=>Assert.Equal(20000m,x.PesoAntesDesconto));
    }
    [Theory]
    [InlineData("Indice","")][InlineData("Indice","3")][InlineData("NrIndiceTotal","2")]
    [InlineData("PercentualDesdobro","101")][InlineData("ContratoACM","")][InlineData("IdParceiro","")]
    public async Task Invalid_item_identity_or_indices_stop_before_persistence(string field,string value)
    {
        var p=Payload();p["to_Pesagem"]![0]![field]=value;var store=new Store();using var services=Services(store);
        Assert.Equal(400,(await ToledoCallbackTests.Post(p.ToJsonString(),services)).Status);Assert.Equal(0,store.Calls);
    }
    [Fact]
    public async Task Duplicate_item_index_and_malformed_collection_fail()
    {
        var p=Payload();p["to_Pesagem"]!.AsArray().Add(p["to_Pesagem"]![0]!.DeepClone());
        foreach(var row in p["to_Pesagem"]!.AsArray())row!["NrIndiceTotal"]="2";
        var store=new Store();using var services=Services(store);Assert.Equal(400,(await ToledoCallbackTests.Post(p.ToJsonString(),services)).Status);
        p=Payload();p["to_QualidPes"]=new JsonObject();Assert.Equal(400,(await ToledoCallbackTests.Post(p.ToJsonString(),services)).Status);
        Assert.Equal(0,store.Calls);
    }
    [Theory]
    [InlineData("ID")][InlineData("Sequencial")][InlineData("EstadoCodigo")][InlineData("Tara")][InlineData("ProtocoloGuardian")]
    public async Task Hybrid_contract_is_rejected_even_if_legacy_properties_are_null(string property)
    {
        var p=Payload();p[property]=null;var store=new Store();using var services=Services(store);
        var result=await ToledoCallbackTests.Post(p.ToJsonString(),services);Assert.Equal(400,result.Status);Assert.Contains("INVALID_CALLBACK_FORMAT",result.Body);Assert.Equal(0,store.Calls);
    }
    [Theory]
    [InlineData("{")][InlineData("null")][InlineData("[]")][InlineData("{\"foo\":1}")]
    public async Task Invalid_or_ambiguous_json_never_persists(string json)
    {var store=new Store();using var services=Services(store);Assert.Equal(400,(await ToledoCallbackTests.Post(json,services)).Status);Assert.Equal(0,store.Calls);}
    [Fact]
    public async Task Duplicate_case_insensitive_property_is_rejected()
    {
        var json=ToledoCallbackTests.ValidJson.Replace("\"IdPesagem\":\"0020318\"","\"IdPesagem\":\"0020318\",\"idpesagem\":\"OTHER\"");
        var store=new Store();using var services=Services(store);Assert.Equal(400,(await ToledoCallbackTests.Post(json,services)).Status);Assert.Equal(0,store.Calls);
    }
    [Fact]
    public async Task Positive_ack_waits_for_confirmed_storage_and_replay_keeps_receipt()
    {
        var store=new Store{Block=true};using var services=Services(store);
        var request=ToledoCallbackTests.Post(ToledoCallbackTests.ValidJson,services);await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(request.IsCompleted);Assert.Empty(store.Rows);store.Commit.TrySetResult();
        var first=await request;var replay=await ToledoCallbackTests.Post(ToledoCallbackTests.ValidJson,services);
        Assert.Equal(200,first.Status);Assert.Equal(200,replay.Status);
        var a=JsonNode.Parse(first.Body)!;var b=JsonNode.Parse(replay.Body)!;
        Assert.Equal((string?)a["receiptId"],(string?)b["receiptId"]);Assert.True((bool)b["idempotent"]!);
        Assert.Equal("RECEIVED_PENDING_CORRELATION",(string?)a["status"]);Assert.Single(store.Rows);
        Assert.Empty(services.GetServices<IHostedService>());
    }
    [Fact]
    public async Task Concurrent_replays_store_one_document_and_divergent_replay_conflicts()
    {
        var store=new Store();using var services=Services(store);
        var replies=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>ToledoCallbackTests.Post(ToledoCallbackTests.ValidJson,services)));
        Assert.All(replies,r=>Assert.Equal(200,r.Status));Assert.Single(store.Rows);
        Assert.Single(replies.Select(r=>(string?)JsonNode.Parse(r.Body)!["receiptId"]).Distinct());
        var changed=Payload();changed["PesoBruto"]="40001.0";
        Assert.Equal(409,(await ToledoCallbackTests.Post(changed.ToJsonString(),services)).Status);Assert.Single(store.Rows);
        var reordered=new JsonObject(Payload().Reverse().Select(x=>new KeyValuePair<string,JsonNode?>(x.Key,x.Value?.DeepClone())));
        Assert.Equal(200,(await ToledoCallbackTests.Post(reordered.ToJsonString(),services)).Status);
    }
    [Fact]
    public async Task Storage_error_or_unknown_commit_does_not_ack_or_leak_details()
    {
        var store=new Store{Fail=true};using var services=Services(store);
        var response=await ToledoCallbackTests.Post(ToledoCallbackTests.ValidJson,services);
        Assert.Equal(503,response.Status);Assert.DoesNotContain("PRIVATE_TEST_MESSAGE",response.Body);Assert.DoesNotContain("persisted",response.Body);Assert.Empty(store.Rows);
    }
    [Fact]
    public async Task Unknown_commit_after_modeled_insert_has_no_positive_ack_or_automatic_retry()
    {
        var store=new Store{FailAfterInsert=true};using var services=Services(store);
        var response=await ToledoCallbackTests.Post(ToledoCallbackTests.ValidJson,services);
        Assert.Equal(503,response.Status);Assert.Equal(1,store.Calls);Assert.Single(store.Rows);
        Assert.DoesNotContain("persisted",response.Body);
    }
    [Fact]
    public async Task Payload_limit_and_wrong_content_type_reject_before_any_store()
    {
        var store=new Store();using var services=Services(store);
        Assert.Equal(413,(await ToledoCallbackTests.Post(new string('x',GuardianCallbackEndpoint.MaxCallbackBytes+1),services)).Status);
        var context=new Microsoft.AspNetCore.Http.DefaultHttpContext{RequestServices=services};
        context.Request.ContentType="text/plain";context.Response.Body=new MemoryStream();
        await GuardianCallbackEndpoint.HandleDualAsync(context);Assert.Equal(415,context.Response.StatusCode);Assert.Equal(0,store.Calls);
    }
    [Fact]
    public void Toledo_is_disabled_by_default_and_requires_a_dedicated_PostgreSql_binding()
    {
        var services=new ServiceCollection();ToledoCallbackComposition.Configure(services,new ConfigurationBuilder().Build(),"InMemory");
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(IRegistroPesagemToledoStore));
        using var disabledProvider=services.BuildServiceProvider(new ServiceProviderOptions{ValidateOnBuild=true,ValidateScopes=true});
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ToledoInbound:Enabled","true"}}).Build();
        Assert.Throws<InvalidOperationException>(()=>ToledoCallbackComposition.Configure(new ServiceCollection(),config,"InMemory"));
        Assert.Throws<InvalidOperationException>(()=>ToledoCallbackComposition.Configure(new ServiceCollection(),config,CallbackHttpOptions.PostgreSqlImplementation));
    }
    [Fact]
    public async Task Real_local_HTTP_route_accepts_Toledo_without_external_gateways_or_worker()
    {
        var store=new Store();var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRegistroPesagemToledoStore>(store);builder.Services.AddScoped<RegistrarPesagemToledoCasoDeUso>();builder.Services.AddSingleton<IToledoCallbackAdmission,OfflineToledoAdmission>();
        await using var app=builder.Build();GuardianCallbackEndpoint.MapDual(app,GuardianCallbackEndpoint.Route);await app.StartAsync();
        try
        {
            var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Assert.Equal("127.0.0.1",new Uri(url).Host);
            using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new Uri(url)};
            using var response=await client.PostAsync(GuardianCallbackEndpoint.Route,new StringContent(ToledoCallbackTests.ValidJson,Encoding.UTF8,"application/json"));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Single(store.Rows);Assert.DoesNotContain(app.Services.GetServices<IHostedService>(),x=>x.GetType().Namespace?.StartsWith("FugaTES")==true);
            var directory=Path.Combine(AppContext.BaseDirectory,"LOCAL_HTTP_EVIDENCE");Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory,"receipt.json"),JsonSerializer.Serialize(new{scope="SYNTHETIC_LOCAL_ONLY_MEMORY_STORE",address=url,http=200,body=await response.Content.ReadAsStringAsync(),rows=store.Rows.Count,externalRequests=0,physicalDatabaseWrites=0}));
        }
        finally{await app.StopAsync();}
    }
    private sealed class Store:IRegistroPesagemToledoStore
    {
        public int Calls;public bool Block,Fail,FailAfterInsert;
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Commit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<(string,string,string),(string Fingerprint,ReciboPesagemToledo Receipt,PesagemToledoRecebida Document)> Rows=[];
        public async Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida doc,string fingerprint,CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);Entered.TrySetResult();if(Block)await Commit.Task.WaitAsync(ct);
            if(Fail)throw new IOException("PRIVATE_TEST_MESSAGE");
            lock(Rows)
            {
                var key=(doc.Origem,doc.Centro,doc.IdPesagem);
                if(Rows.TryGetValue(key,out var row)){if(row.Fingerprint!=fingerprint)throw new PesagemToledoConflitoException();return row.Receipt with{Idempotente=true};}
                var receipt=new ReciboPesagemToledo(Guid.NewGuid(),DateTimeOffset.UtcNow,false);Rows.Add(key,(fingerprint,receipt,doc));
                if(FailAfterInsert)throw new IOException("PRIVATE_TEST_MESSAGE_UNKNOWN_COMMIT");return receipt;
            }
        }
    }
}
