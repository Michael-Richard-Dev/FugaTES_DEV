using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.Guardian;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class R165AILocalAutomaticEndToEndTests
{
    [Theory]
    [InlineData("success", "0000986701")]
    [InlineData("http400", "0000986702")]
    [InlineData("unknown", "0000986703")]
    public async Task Local_POST_drives_existing_worker_and_actual_adapters_with_no_external_transport(string outcome,string number)
        => await RunScenarioAsync(outcome,number);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage","xUnit1013",Justification="Explicit opt-in local demo harness entrypoint; not a test case")]
    public static async Task RunScenarioAsync(string outcome,string number,Action<string,object>? observer=null,CancellationToken stop=default,bool keepAlive=false)
    {
        void Emit(string name,object data)=>observer?.Invoke(name,data);
        var f=new AutomaticFixture();var repo=new RepositorioMotorIntegracaoFake();
        var window=R165AGWindowSelectionTests.Window();window.Item=null;
        var store=new CommittedStore(f.Versions,repo);
        store.AfterCommit=v=>Emit("H4_MODELLED_COMMIT",new{integrationId=v.IntegracaoId,correlationId=v.AdmissionCorrelationId,fingerprint=v.FingerprintSha256,h4="H4v1 COMMIT MODELADO EM MEMORIA",reception="POST RECEBIDO; ACK AINDA PENDENTE"});
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older=f.Add("1147").Version;PilotAutomaticSelectionTests.Seed(repo,older);
        using var wire=new GuardianWire(f,store,outcome,observer);
        using var intercepted=new HttpClient(wire); // No SocketsHttpHandler exists for Guardian.
        var guardianOptions=new GuardianHttpOptions
        {
            BaseUrl=new("https://guardian.invalid"),Timeout=TimeSpan.FromSeconds(2),
            PreCadastroEndpoint="/api/v1/Guardian/ProtocoloPreCadastro",
            ContratoSoapEndpoint=new("http://192.168.17.91/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx"),
            ContratoSoapEnvironment="HML",ContratoSoapInternalHmlHttpApproved=true
        };
        var soap=new GuardianContratoSoapClient(intercepted,guardianOptions);
        var rest=new GuardianSet26HttpClient(intercepted,guardianOptions,new GuardianTokenClient(intercepted,guardianOptions,new Credentials()));
        var normal=R165AGNormalWindowTests.Options(window) with
        {
            IdAgenda=1,
            ApprovedWindowPartnerMappings=[new(){WindowId=window.WindowId,Centro="3011",Item=null,
                SapPartnerId="0020000051",GuardianCode="20000051",EvidenceReference="SYNTHETIC_KNOWN_MAPPING_SHAPE_NOT_LIVE_APPROVAL"}],
            ApprovedWindowMaterialMappings=[new(){WindowId=window.WindowId,Centro="3011",Item=null,
                SapMaterial="7000000",GuardianCode="7000000",EvidenceReference="SYNTHETIC_KNOWN_MAPPING_SHAPE_NOT_LIVE_APPROVAL"}]
        };
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(R165AGInboundWindowTests.Config(new()
        {
            ["HostIntegracao:PersistenceMode"]="Fake",["HostIntegracao:ExternalMode"]="Fake",
            ["HostIntegracao:PollInterval"]="00:00:00.020",
            ["MotorIntegracao:AcmInboundWindow:WindowId"]=window.WindowId,
            ["MotorIntegracao:AcmInboundWindow:AuthorizationReference"]=window.AuthorizationReference,
            ["MotorIntegracao:AcmInboundWindow:NotBeforeUtc"]=window.NotBeforeUtc!.Value.ToString("O"),
            ["MotorIntegracao:AcmInboundWindow:UntilUtc"]=window.UntilUtc!.Value.ToString("O")
        }));
        var services=builder.Services;
        services.AddSingleton<IContratoSapToledoVersaoStore>(store);
        services.AddSingleton<ICadastraContratoGuardianGateway>(soap);
        services.AddSingleton<IAcmPreCadastroMinimoGatewayR57>(rest);
        services.AddSingleton<IAcmGuardianVinculoStore>(f.H4);
        services.AddSingleton<IResultadoFluxoMvpStore>(f.Journal);
        services.AddSingleton(normal);services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
        HostIntegracaoComposition.ConfigurarServicos(services,builder.Configuration);
        Assert.Single(services,s=>s.ServiceType==typeof(IHostedService));
        services.Replace(ServiceDescriptor.Singleton<IMotorIntegracaoRepository>(repo));
        services.Replace(ServiceDescriptor.Scoped<IProcessadorExternoIntegracao>(sp=>new RecordClaim(
            sp.GetRequiredService<ProcessadorFluxosMvp>(),f.Journal,completed,observer)));
        await using var app=builder.Build();SapContractInboundEndpoint.MapDurable(app);
        app.MapGet("/r165aj/health",()=>new{scope="SIMULATION_LOCAL_ONLY",workerCount=1});
        await app.StartAsync();
        string? address=null;
        try
        {
            address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri=new Uri(address);Assert.Equal("127.0.0.1",uri.Host);
            Assert.DoesNotContain(uri.Port,new[]{8080,18085,18185,15434});
            using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false})
                {BaseAddress=uri,Timeout=TimeSpan.FromSeconds(10)};
            http.DefaultRequestHeaders.Authorization=System.Net.Http.Headers.AuthenticationHeaderValue.Parse(R165AGInboundAuthScopeTests.Basic);
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto","https"); // Synthetic proxy handoff, not TLS proof.
            Emit("LOCAL_POST_STARTED",new{address,reception="POST LOCAL ENVIADO"});
            var sending=http.PostAsJsonAsync(SapContractInboundEndpoint.Route,Payload(number));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var concurrent=http.PostAsJsonAsync(SapContractInboundEndpoint.Route,Payload(number));
            Assert.False(store.Committed);Assert.Equal(0,wire.SoapCreates);Assert.Equal(0,wire.RestCreates);
            Assert.NotSame(sending,await Task.WhenAny(sending,Task.Delay(100)));
            Assert.NotSame(concurrent,await Task.WhenAny(concurrent,Task.Delay(100)));
            if(observer is not null)await Task.Delay(800,stop);
            store.AllowCommit.TrySetResult();
            using var first=await sending;using var repeated=await concurrent;
            Assert.Equal(HttpStatusCode.Created,first.StatusCode);Assert.Equal(HttpStatusCode.Created,repeated.StatusCode);
            using var ack=JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            using var ack2=JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
            Assert.Equal(ack.RootElement.GetProperty("correlationId").GetString(),ack2.RootElement.GetProperty("correlationId").GetString());
            Assert.Equal(Assert.Single(store.Rows).CorrelationId,ack.RootElement.GetProperty("correlationId").GetString());
            Emit("ACK_AFTER_MODELLED_COMMIT",new{reception="HTTP 201 APOS COMMIT MODELADO",integrationId=store.AdmittedId,correlationId=store.Rows[0].CorrelationId});
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var expected=outcome switch {"success"=>EstadoIntegracao.Concluida,"http400"=>EstadoIntegracao.FalhaDefinitiva,_=>EstadoIntegracao.ResultadoIndeterminado};
            try { await WaitForFinalState(repo,store.AdmittedId,expected); } catch { throw new InvalidOperationException(JsonSerializer.Serialize(new { observed=(await repo.ObterPorIdAsync(store.AdmittedId,default))?.Estado.ToString(), journal=f.Journal.Results.GetValueOrDefault(store.AdmittedId), wire.SoapCreates, wire.RestCreates })); }
            var link=Assert.Single(f.H4.Items.Values);var journal=f.Journal.Results[store.AdmittedId];
            Assert.Equal(store.AdmittedId,link.IntegracaoIdOrigem);
            Assert.Equal(store.Rows[0].CorrelationId,f.Journal.Claims[store.AdmittedId].CorrelationId);
            Assert.Equal(store.Rows[0].CorrelationId,journal.Correlacao);
            Assert.Equal(number,link.NumeroContratoSap);Assert.Equal(number,link.CodigoContratoConfirmado);
            Assert.Equal("OC"+number.TrimStart('0'),link.NumeroReferenciaSolicitado);
            Assert.Equal(1,wire.Lookups);Assert.Equal(1,wire.SoapCreates);Assert.Equal(1,wire.RestCreates);Assert.Equal(1,wire.TokenCalls);
            Assert.Equal(1,Assert.Single(repo.Tentativas).FencingToken);
            Assert.Equal(EstadoIntegracao.Pendente,(await repo.ObterPorIdAsync(older.IntegracaoId,default))!.Estado);
            if(outcome=="success")
            {
                Assert.Equal(99077,link.ProtocoloIdGuardian);Assert.Equal("99077",journal.ProtocoloGuardian);
                Assert.Equal("R165U_CONFIRMED",journal.Etapa);
            }
            else
            {
                Assert.Null(link.ProtocoloIdGuardian);
                Assert.Equal(outcome=="http400"?StatusFluxoMvp.FalhaNaoReprocessavel:StatusFluxoMvp.ResultadoIndeterminado,journal.Status);
                if(outcome=="http400")
                {
                    var diagnostic=Assert.IsType<GuardianR57ExchangeDiagnostic>(rest.LastR57Diagnostic);
                    Assert.Equal(400,diagnostic.HttpStatus);
                    Assert.Equal("The codigoContrato field is invalid.",diagnostic.SafeBusinessMessage);
                    Assert.DoesNotContain(GuardianWire.Token,diagnostic.SafeResponseBody??"");
                    Assert.DoesNotContain("synthetic@example.invalid",diagnostic.SafeResponseBody??"");
                }
            }
            using var replay=await http.PostAsJsonAsync(SapContractInboundEndpoint.Route,Payload(number));
            Assert.Equal(HttpStatusCode.Created,replay.StatusCode);
            using var secondContract=await http.PostAsJsonAsync(SapContractInboundEndpoint.Route,Payload("0000986799"));
            Assert.Equal(HttpStatusCode.Conflict,secondContract.StatusCode);
            using var divergent=await http.PostAsJsonAsync(SapContractInboundEndpoint.Route,Payload(number,20000m));
            Assert.Equal(HttpStatusCode.Conflict,divergent.StatusCode);
            // Let the real hosted worker poll after terminal/unknown outcome: no new claim or send.
            await Task.Delay(150);Assert.Equal(1,wire.SoapCreates);Assert.Equal(1,wire.RestCreates);Assert.Single(repo.Tentativas);
            var later=await repo.SelecionarCandidatosPreClaimAsync(DateTimeOffset.UtcNow.AddHours(2),default);
            Assert.DoesNotContain(later,x=>x.Id==store.AdmittedId);
            var receipt=new
            {
                scope="SYNTHETIC_LOCAL_HARNESS_NOT_SAP_CPI_DELIVERY",outcome,address,contract=number,
                localPostHttp=201,concurrentRedeliveryHttp=201,identicalReplayHttp=201,
                input=Payload(number),integrationId=store.AdmittedId,correlationId=store.Rows[0].CorrelationId,
                h4Version=1,h4Fingerprint=store.Rows[0].Version!.FingerprintSha256,
                h4Commit="MODELLED_COMMIT_IN_EXISTING_IN_MEMORY_DOUBLE_NOT_PHYSICAL_POSTGRESQL",
                guardianAdapters="REAL_SOAP_REST_TOKEN_CLASSES_WITH_IN_PROCESS_HTTP_HANDLER_ONLY",
                wire.Lookups,wire.SoapCreates,wire.TokenCalls,wire.RestCreates,simulatedProtocolId=link.ProtocoloIdGuardian,
                journalStage=journal.Etapa,journalStatus=journal.Status.ToString(),journalError=journal.ErroCodigo,
                journalCorrelation=journal.Correlacao,claimCorrelation=f.Journal.Claims[store.AdmittedId].CorrelationId,
                safeHttpStatus=outcome=="http400"?400:outcome=="success"?200:0,
                safeBusinessMessage=outcome=="http400"?rest.LastR57Diagnostic!.SafeBusinessMessage:null,
                motorState=expected.ToString(),attempts=repo.Tentativas.Count(),workerCount=1,backlog="UNCLAIMED",
                concurrentRedelivery="SAME_DURABLE_IDENTITY",divergentPayload="HTTP409",secondContract="HTTP409",
                manualIntervention=false,realGuardianRequests=0,hmlDatabaseWrites=0
            };
            var directory=Path.Combine(AppContext.BaseDirectory,"R165AI_RECEIPTS");Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,outcome+".json"),JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true}));
            Emit("TERMINAL_JOURNAL_OBSERVED",new{integrationId=store.AdmittedId,correlationId=journal.Correlacao,
                protocolId=link.ProtocoloIdGuardian,journalStage=journal.Etapa,journalStatus=journal.Status.ToString(),
                journalError=journal.ErroCodigo,motorState=outcome=="success"?"CONCLUIDA":outcome=="http400"?"FALHA_DEFINITIVA":"RESULTADO_INDETERMINADO",
                httpStatus=outcome=="unknown"?(int?)null:outcome=="http400"?400:200,
                rest=outcome=="unknown"?"RESULTADO INDETERMINADO - SEM RETRY":outcome=="http400"?"HTTP 400 TERMINAL - SEM RETRY":"HTTP 200; ID PERSISTIDO NO DOUBLE",
                safeBusinessMessage=outcome=="http400"?rest.LastR57Diagnostic!.SafeBusinessMessage:null,
                attempts=repo.Tentativas.Count(),soapCreates=wire.SoapCreates,restCreates=wire.RestCreates,workerCount=1});
            if(keepAlive)await Task.Delay(Timeout.Infinite,stop);
        }
        finally{store.AllowCommit.TrySetResult();await app.StopAsync();}
    }

    private static object Payload(string number,decimal quantity=10000m)=>new{Contrato=new[]{new{
        NumeroContrato=number,TipoContrato="AC3P",DescricaoTipoContrato="SYNTHETIC LOCAL TEST NOT SAP DELIVERY",
        Centro="3011",TipoPesagem="E",OperacaoContrato="10",StatusContrato="T",IdParceiro="0020000051",Material="7000000",QuantidadeContrato=quantity}}};
    private static async Task WaitForFinalState(RepositorioMotorIntegracaoFake repo,Guid id,EstadoIntegracao expected)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while((await repo.ObterPorIdAsync(id,timeout.Token))?.Estado!=expected)await Task.Delay(10,timeout.Token);
    }
    private sealed class RecordClaim(ProcessadorFluxosMvp dispatcher,AutomaticFixture.Ledger journal,TaskCompletionSource completed,Action<string,object>? observer):IProcessadorExternoIntegracao
    {
        public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim,CancellationToken ct)
        {
            journal.Claims[claim.IntegracaoId]=claim;
            observer?.Invoke("AUTOMATIC_WORKER_CLAIM",new{integrationId=claim.IntegracaoId,correlationId=claim.CorrelationId,worker="CLAIM AUTOMATICO; FENCING "+claim.FencingToken,motorState="EM_PROCESSAMENTO"});
            if(observer is not null)await Task.Delay(800,ct);
            try{var result=await dispatcher.ProcessarAsync(claim,ct);completed.TrySetResult();return result;}
            catch(Exception error){completed.TrySetException(error);throw;}
        }
    }
    private sealed class Credentials:ICredenciaisGuardianSet26Provider
    {public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("synthetic@example.invalid","SYNTHETIC_NOT_A_PASSWORD","SYNTHETIC_SIGNATURE"));}

    private sealed class GuardianWire(AutomaticFixture f,CommittedStore store,string outcome,Action<string,object>? observer):HttpMessageHandler
    {
        public const string Token="SYNTHETIC_R165AI_TOKEN_NOT_A_CREDENTIAL";
        public int Lookups,SoapCreates,TokenCalls,RestCreates;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Headers.TryGetValues("SOAPAction",out var actions))
            {
                Assert.Equal("192.168.17.91",request.RequestUri!.Host);
                var action=actions.Single();var xml=XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if(action.EndsWith("/ConsultaContrato\"",StringComparison.Ordinal))
                {Lookups++;return Soap("ConsultaContrato","<Erro>309851</Erro><Descricao>O código do contrato informado não existe!</Descricao>");}
                Assert.EndsWith("/CadastraContrato\"",action);Assert.True(store.Committed);
                Assert.Equal("R165U_SOAP_ARMED",f.Journal.Results[store.AdmittedId].Etapa);
                SoapCreates++;
                observer?.Invoke("SOAP_SIMULATED_RESPONSE_200",new{soap="CADASTRA CONTRATO: RESPOSTA SOAP SIMULADA 200",journalStage=f.Journal.Results[store.AdmittedId].Etapa});
                if(observer is not null)await Task.Delay(800,ct);
                var code=xml.Descendants().Single(x=>x.Name.LocalName=="Contrato").Elements().Single(x=>x.Name.LocalName=="Codigo").Value;
                return Soap("CadastraContrato","<Erro>0</Erro><Contrato><Codigo>"+code+"</Codigo><Sequencial>900001</Sequencial></Contrato>");
            }
            Assert.Equal("guardian.invalid",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);
            if(request.RequestUri.AbsolutePath=="/api/v2/CreateToken")
            {TokenCalls++;return Json(200,JsonSerializer.Serialize(new{token=Token}));}
            Assert.Equal("/api/v1/Guardian/ProtocoloPreCadastro",request.RequestUri.AbsolutePath);
            Assert.Equal("R165U_REST_ARMED",f.Journal.Results[store.AdmittedId].Etapa);Assert.Equal(1,SoapCreates);
            RestCreates++;Assert.Equal(Token,request.Headers.Authorization?.Parameter);
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var data=body.RootElement.GetProperty("dadosProtocoloPreCadastro");
            Assert.Equal(f.H4.Items[store.AdmittedId].CodigoContratoConfirmado,data.GetProperty("codigoContrato").GetString());
            observer?.Invoke("REST_SIMULATED_REQUEST",new{soap="SUCESSO SOAP CONFIRMADO PELO PIPELINE",rest="POST SIMULADO EM ANDAMENTO",payloadJson=body.RootElement.GetRawText(),journalStage=f.Journal.Results[store.AdmittedId].Etapa});
            if(observer is not null)await Task.Delay(800,ct);
            if(outcome=="unknown")throw new OperationCanceledException("SYNTHETIC_UNCERTAIN_REST_RESPONSE");
            if(outcome=="http400")return Json(400,JsonSerializer.Serialize(new{errors=new Dictionary<string,string[]>{{"codigoContrato",["The codigoContrato field is invalid."]}},token=Token,email="synthetic@example.invalid"}));
            return Json(200,JsonSerializer.Serialize(new{id=99077,codigoContrato=data.GetProperty("codigoContrato").GetString(),
                codigoItem=data.GetProperty("codigoItem").GetString(),codigoEmissor=data.GetProperty("codigoEmissor").GetString(),numeroReferencia=data.GetProperty("numeroReferencia").GetString()}));
        }
        private static HttpResponseMessage Json(int code,string json)=>new((HttpStatusCode)code){Content=new StringContent(json,Encoding.UTF8,"application/json")};
        private static HttpResponseMessage Soap(string operation,string inner)=>new(HttpStatusCode.OK){Content=new StringContent(
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><"+operation+"Response xmlns=\"http://toledobrasil.com.br/WS_Guardian\"><voRetornoContrato><VORetornoContrato>"+inner+
            "</VORetornoContrato></voRetornoContrato></"+operation+"Response></s:Body></s:Envelope>",Encoding.UTF8,"text/xml")};
    }
    private sealed class CommittedStore(AutomaticFixture.TestVersions versions,RepositorioMotorIntegracaoFake repo):IContratoSapToledoAdmissionStore
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowCommit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<VersaoContratoSapToledo>? AfterCommit;
        public readonly List<AcmInboundAdmittedRow> Rows=[];public bool Committed;public Guid AdmittedId;
        public async Task<VersaoContratoSapToledo> RegistrarPrimeiroOuObterReentregaAsync(ContratoSapToledoSnapshot s,string fp,string payload,string correlation,AcmInboundAdmission admission,CancellationToken ct)
        {
            Entered.TrySetResult();await AllowCommit.Task.WaitAsync(ct);
            lock(versions.Items)
            {
                var now=DateTimeOffset.UtcNow;var old=AcmInboundAdmissionGuard.Resolver(admission,s,fp,correlation,now,Rows);if(old is not null)return old;
                var v=new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,s.NumeroContrato!,1,fp,Guid.NewGuid(),s){AdmissionCorrelationId=correlation};
                versions.Items[v.IntegracaoId]=v;Rows.Add(new(v,correlation,now,"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",s.NumeroContrato!,fp));
                AdmittedId=v.IntegracaoId;Committed=true;
                repo.Semear(new(v.IntegracaoId,"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",v.NumeroContrato,fp,correlation,EstadoIntegracao.Pendente,TipoDisparoPendente.Automatico,now,0,null,null,FaseEnvioIntegracao.NaoIniciado,null));
                AfterCommit?.Invoke(v);
                return v;
            }
        }
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot s,string f,string p,string c,CancellationToken ct)=>throw new InvalidOperationException("TYPED_ADMISSION_ONLY");
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct){lock(versions.Items)return Task.FromResult(versions.Items.GetValueOrDefault(id));}
        public Task<bool> TemPredecessorNaoTerminalAsync(string n,long v,CancellationToken ct)=>Task.FromResult(false);
    }
}
