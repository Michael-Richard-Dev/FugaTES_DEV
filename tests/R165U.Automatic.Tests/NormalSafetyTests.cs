using System.Net;
using System.Text;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class NormalSafetyTests
{
    [Theory]
    [InlineData("enabled")] [InlineData("authorization")] [InlineData("reference")]
    [InlineData("guardian")] [InlineData("database")] [InlineData("journal")]
    [InlineData("mapping")] [InlineData("proximity")] [InlineData("value")]
    public async Task Missing_approval_or_provenance_never_produces_external_writes(string field)
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();var options=AutomaticFixture.Options();
        options=field switch
        {
            "enabled"=>options with{Enabled=false},"authorization"=>options with{ExecutionAuthorized=false},
            "reference"=>options with{AuthorizationReference=null},"guardian"=>options with{GuardianIdentityEvidence=null},
            "database"=>options with{PostgreSqlIdentityEvidence=null},"journal"=>options with{JournalEvidence=null},
            "mapping"=>options with{IdentifierMappingEvidence=null},"proximity"=>options with{ProximityValueEvidence=null},
            _=>options with{ValorProximidadeFimContrato=null}
        };
        var r=await f.Pipeline(options).ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,r.Status);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Rest.Creates);
    }

    [Theory]
    [InlineData(null)] [InlineData("false")] [InlineData("invalid")]
    public void Existing_worker_is_not_registered_by_absent_false_or_invalid_flag(string? flag)
    {
        var services=new ServiceCollection();var configuration=WorkerConfig(flag);
        HostIntegracaoComposition.ConfigurarServicos(services,configuration);
        Assert.DoesNotContain(services,d=>d.ServiceType==typeof(IHostedService));
    }

    [Fact]
    public void Explicit_test_flag_registers_only_the_existing_worker_without_starting_it()
    {
        var services=new ServiceCollection();HostIntegracaoComposition.ConfigurarServicos(services,WorkerConfig("true"));
        var descriptor=Assert.Single(services,d=>d.ServiceType==typeof(IHostedService));
        Assert.Equal(typeof(HospedagemIntegracaoWorker),descriptor.ImplementationType);
    }

    private static IConfiguration WorkerConfig(string? flag)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["HostIntegracao:PersistenceMode"]="Fake",["HostIntegracao:ExternalMode"]="Fake",
        ["HostIntegracao:PollInterval"]="00:00:05",["HostIntegracao:ShutdownGracePeriod"]="00:00:10",
        ["MotorIntegracao:LeaseDuration"]="00:01:00",["MotorIntegracao:LeaseRenewInterval"]="00:00:20",
        ["MotorIntegracao:RetryBackoff"]="00:01:00",["TimeoutProcessamentoIntegracao:Timeout"]="00:00:10",
        ["FUGATES_INTEGRATION_WORKER_ENABLED"]=flag
    }).Build();

    [Fact]
    public async Task Normal_host_dispatch_invokes_existing_contract_case_and_generic_pipeline()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();
        var services=new ServiceCollection();services.AddSingleton<IMotorIntegracaoRepository,RepositorioMotorIntegracaoFake>();
        services.AddSingleton(TimeProvider.System);services.AddSingleton(AutomaticFixture.Options());
        services.AddSingleton<IContratoSapToledoVersaoStore>(f.Versions);services.AddSingleton<IAcmGuardianVinculoStore>(f.H4);
        services.AddSingleton<IResultadoFluxoMvpStore>(f.Journal);services.AddSingleton<ICadastraContratoGuardianGateway>(f.Soap);
        services.AddSingleton<IAcmPreCadastroMinimoGatewayR57>(f.Rest);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["AcmR57:Enabled"]="true",["AcmR57:Normal:Enabled"]="true" }).Build();
        services.AddMvpOrchestration(config);
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        var result=await scope.ServiceProvider.GetRequiredService<ProcessadorFluxosMvp>().ProcessarAsync(c,default);
        Assert.Null(result.Falha);Assert.Equal("1147",f.Soap.Sent!.Codigo);Assert.Equal(1,f.Rest.Creates);
        Assert.DoesNotContain(services,d=>d.ServiceType==typeof(IHostedService));
    }

    [Fact]
    public async Task Concurrent_replay_of_armed_soap_does_not_create_twice()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Soap.BeforeCreate=()=>{entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();};
        var first=Task.Run(()=>f.Pipeline().ExecutarAsync(c,default));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,(await f.Pipeline().ExecutarAsync(c,default)).Status);
        }
        finally{release.Set();}
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,(await first).Status);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
    }

    [Fact]
    public async Task Predecessor_and_failed_rest_reservation_are_safe()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();f.Versions.Predecessor=true;
        Assert.Equal("R165U_PREDECESSOR_PENDING",(await f.Pipeline().ExecutarAsync(c,default)).ErroCodigo);
        Assert.Equal(0,f.Soap.Lookups);f.Versions.Predecessor=false;f.Journal.RejectStage="R165U_REST_ARMED";
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,(await f.Pipeline().ExecutarAsync(c,default)).Status);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }

    [Theory]
    [InlineData("1147",EstadoConsultaContratoGuardian.InexistenteComprovado)]
    [InlineData("1148",EstadoConsultaContratoGuardian.InexistenteComprovado)]
    [InlineData("1106",EstadoConsultaContratoGuardian.InexistenteComprovado)]
    public async Task Real_soap_parser_scopes_proven_absence_to_the_requested_contract(string code,EstadoConsultaContratoGuardian expected)
    {
        using var http=new HttpClient(new SyntheticSoap());
        var gateway=new GuardianContratoSoapClient(http,new GuardianHttpOptions
        {
            ContratoSoapEndpoint=new("http://192.168.17.91/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx"),
            ContratoSoapEnvironment="HML",ContratoSoapInternalHmlHttpApproved=true,Timeout=TimeSpan.FromSeconds(2)
        });
        Assert.Equal(expected,(await gateway.ConsultarAsync(code,default)).Estado);
    }

    private sealed class SyntheticSoap:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(!request.Headers.GetValues("SOAPAction").Single().Contains("ConsultaContrato"))
                throw new InvalidOperationException("NO_OTHER_WIRE_OPERATION_IN_SYNTHETIC_TEST");
            var xml="""
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                  <s:Body><ConsultaContratoResponse xmlns="http://toledobrasil.com.br/WS_Guardian">
                    <voRetornoContrato><VORetornoContrato><Erro>309851</Erro><Descricao>O código do contrato informado não existe!</Descricao></VORetornoContrato></voRetornoContrato>
                  </ConsultaContratoResponse></s:Body>
                </s:Envelope>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(xml,Encoding.UTF8,"text/xml")});
        }
    }

    [Fact]
    public async Task Historical_1106_emitter_mapping_is_not_silently_replaced_by_generic_verbatim_copy()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add("0000001106",s=>s with{IdParceiro="0020000184"});
        var result=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,result.Status);
        Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }

    [Fact]
    public async Task Cancellation_after_soap_success_preserves_identity_and_never_starts_rest()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();using var cts=new CancellationTokenSource();
        f.Soap.AfterResponse=cts.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Pipeline().ExecutarAsync(c,cts.Token));
        Assert.Equal("1147",f.H4.Items[v.IntegracaoId].CodigoContratoConfirmado);
        Assert.Equal("R165U_SOAP_CONFIRMED",f.Journal.Results[v.IntegracaoId].Etapa);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }

    [Fact]
    public async Task Cancellation_after_rest_success_preserves_protocol_ticket_and_confirmed_journal()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();using var cts=new CancellationTokenSource();
        f.Rest.AfterResponse=cts.Cancel;
        var result=await f.Pipeline().ExecutarAsync(c,cts.Token);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,result.Status);
        Assert.Equal(801,f.H4.Items[v.IntegracaoId].ProtocoloIdGuardian);
        Assert.Equal("SYNTHETIC-TICKET",f.H4.Items[v.IntegracaoId].NumeroTicketGuardian);
        Assert.Equal("R165U_CONFIRMED",f.Journal.Results[v.IntegracaoId].Etapa);
        Assert.Equal(result,await f.Pipeline().ExecutarAsync(c,default));Assert.Equal(1,f.Rest.Creates);
    }

    [Fact]
    public async Task Post_response_capture_does_not_override_a_lost_fenced_claim()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();using var cts=new CancellationTokenSource();
        f.Rest.AfterResponse=()=>{cts.Cancel();f.Journal.Claims[v.IntegracaoId]=c with{FencingToken=2,Worker="OTHER"};};
        var result=await f.Pipeline().ExecutarAsync(c,cts.Token);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,result.Status);
        Assert.Equal(801,f.H4.Items[v.IntegracaoId].ProtocoloIdGuardian);
        Assert.Equal("R165U_REST_ARMED",f.Journal.Results[v.IntegracaoId].Etapa);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
    }
}
