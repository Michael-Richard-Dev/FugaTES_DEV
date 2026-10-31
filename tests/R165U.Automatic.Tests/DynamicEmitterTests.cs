using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public sealed class DynamicEmitterTests
{
    [Fact] public async Task New_partner_not_in_whitelist_can_complete_pipeline_only_after_active_get_proof()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add("2819",s=>s with{IdParceiro="0020000169"});Stamp(v,DateTimeOffset.UtcNow);
        var o=Options();var lookup=new Lookup();var resolver=Resolver(o,lookup);var pipeline=Pipeline(f,o,resolver);
        var result=await pipeline.ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,result.Status);Assert.Equal(1,lookup.Calls);
        Assert.Equal("20000169",lookup.Codigo);Assert.Equal("20000169",f.Soap.Sent!.CodigoEmissor);Assert.Equal("20000169",f.Rest.Sent!.Dados.CodigoEmissor);
        Assert.Single(o.ApprovedMaterialMappings);Assert.Empty(o.ApprovedPartnerMappings);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
    }
    [Theory][InlineData("R171_EMITTER_NOT_FOUND")][InlineData("R171_EMITTER_INACTIVE")][InlineData("R171_EMITTER_LOOKUP_UNVERIFIED")][InlineData("R171_EMITTER_AUTHENTICATION_FAILED")][InlineData("R171_EMITTER_IDENTITY_MISMATCH")]
    public async Task Failed_emitter_validation_blocks_before_contract_lookup_or_writes(string reason)
    {var f=new AutomaticFixture();var(v,c)=f.Add("2820",s=>s with{IdParceiro="0020000169"});Stamp(v,DateTimeOffset.UtcNow);var o=Options();var lookup=new Lookup{Result=new(false,"",0,133289,404,reason)};var result=await Pipeline(f,o,Resolver(o,lookup)).ExecutarAsync(c,default);Assert.Equal(reason,result.Erro);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);Assert.Empty(f.H4.Items);}
    [Fact] public async Task Runtime_disabled_does_not_even_query_the_emitter()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2821");Stamp(v,DateTimeOffset.UtcNow);var o=Options() with{ExecutionAuthorized=false};var lookup=new Lookup();var result=await Pipeline(f,o,Resolver(o,lookup)).ExecutarAsync(c,default);Assert.Equal("R165U_EXECUTION_DISABLED",result.Erro);Assert.Equal(0,lookup.Calls);}
    [Fact] public async Task Old_1175_like_block_is_not_liberated_by_deployment()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2822",s=>s with{IdParceiro="0020000169"});Stamp(v,DateTimeOffset.UtcNow.AddDays(-2));var o=Options();var lookup=new Lookup();var result=await Pipeline(f,o,Resolver(o,lookup)).ExecutarAsync(c,default);Assert.Equal("R170_PARTNER_MAPPING_REQUIRED",result.Erro);Assert.Equal(0,lookup.Calls);Assert.Equal(0,f.Soap.Creates);}
    [Fact] public async Task Unknown_external_stage_never_queries_or_repeats_soap()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2823");Stamp(v,DateTimeOffset.UtcNow);f.Journal.Results[c.IntegracaoId]=new ResultadoFluxoMvp(StatusFluxoMvp.ResultadoIndeterminado,c.CorrelationId,"R165U_SOAP_ARMED",Erro:"UNKNOWN");var o=Options();var lookup=new Lookup();Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,(await Pipeline(f,o,Resolver(o,lookup)).ExecutarAsync(c,default)).Status);Assert.Equal(0,lookup.Calls);Assert.Equal(0,f.Soap.Creates);}
    [Fact] public async Task Confirmed_soap_is_not_repeated_when_recovering_rest()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2824",s=>s with{IdParceiro="0020000169"});Stamp(v,DateTimeOffset.UtcNow);var o=Options();var lookup=new Lookup();await Pipeline(f,o with{CallbackApproved=false},Resolver(o,lookup)).ExecutarAsync(c,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal("R165U_PROTOCOL_BLOCKED",f.Journal.Results[c.IntegracaoId].Etapa);var result=await Pipeline(f,o,Resolver(o,new Lookup())).ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,result.Status);Assert.Equal(1,f.Soap.Creates);}
    [Fact] public async Task Foreign_signature_response_cannot_become_a_mapping()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2825");Stamp(v,DateTimeOffset.UtcNow);var o=Options();var lookup=new Lookup{Result=new(true,"20000051",1,999,200,"R171_EMITTER_CONFIRMED")};var result=await Pipeline(f,o,Resolver(o,lookup)).ExecutarAsync(c,default);Assert.Equal("R171_EMITTER_SCOPE_MISMATCH",result.Erro);Assert.Equal(0,f.Soap.Creates);}
    [Fact] public async Task Proof_for_another_integration_or_fingerprint_is_rejected()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2826",s=>s with{IdParceiro="0020000169"});Stamp(v,DateTimeOffset.UtcNow);var o=Options();dynamic resolver=Resolver(o,new Lookup());await resolver.ValidarAsync(v,CancellationToken.None);var other=v with{IntegracaoId=Guid.NewGuid()};var gate=Gate(o,resolver);Assert.Equal("R171_EMITTER_VALIDATION_REQUIRED",gate.Bloqueio(other));}
    [Fact] public async Task Proof_expires_before_any_later_write_guard_can_reuse_it()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2827");Stamp(v,DateTimeOffset.UtcNow);var o=Options();var clock=new Clock();dynamic resolver=Resolver(o,new Lookup{Result=new(true,"20000051",1,133289,200,"R171_EMITTER_CONFIRMED")},clock);await resolver.ValidarAsync(v,CancellationToken.None);Assert.Null(Gate(o,resolver).Bloqueio(v));clock.Now=clock.Now.AddMinutes(3);Assert.Equal("R171_EMITTER_PROOF_EXPIRED",Gate(o,resolver).Bloqueio(v));}
    [Fact] public async Task Material_guard_is_not_relaxed_by_dynamic_partner_proof()
    {var f=new AutomaticFixture();var(v,c)=f.Add("2828",s=>s with{Material="9999999"});Stamp(v,DateTimeOffset.UtcNow);var o=Options();var lookup=new Lookup();var result=await Pipeline(f,o,Resolver(o,lookup)).ExecutarAsync(c,default);Assert.Equal("R170_MATERIAL_MAPPING_REQUIRED",result.Erro);Assert.Equal(0,lookup.Calls);Assert.Equal(0,f.Soap.Creates);}
    [Fact] public async Task Host_gate_and_preparation_share_proof_only_inside_the_same_scope()
    {
        var o=Options();var lookup=new Lookup();var f=new AutomaticFixture();var(v,c)=f.Add("2829",s=>s with{IdParceiro="0020000169"});Stamp(v,DateTimeOffset.UtcNow);
        var services=new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services,o);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IGuardianEmitterRead>(services,lookup);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services,TimeProvider.System);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<FugaTES.Aplicacao.Contratos.MotorIntegracao.IMotorIntegracaoRepository>(services,new FugaTES.Infraestrutura.MotorIntegracao.RepositorioMotorIntegracaoFake());
        var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["AcmR57:Enabled"]="true",["AcmR57:Normal:Enabled"]="true",["AcmR57:Normal:DynamicEmitterValidationEnabled"]="true"}).Build();
        FugaTES.HostIntegracao.MvpOrchestrationComposition.AddMvpOrchestration(services,config);
        using var provider=services.BuildServiceProvider();using(var scope=provider.CreateScope())
        {
            var gate=scope.ServiceProvider.GetRequiredService<IAcmR57RuntimeGate>();var prep=scope.ServiceProvider.GetRequiredService<IAcmR57Preparacao>();
            Assert.Equal("R171_EMITTER_VALIDATION_REQUIRED",gate.Bloqueio(v));await ((IAcmR57DynamicEmitterPreparation)prep).ValidarEmissorAsync(v,default);Assert.Null(gate.Bloqueio(v));
        }
        using var next=provider.CreateScope();Assert.Equal("R171_EMITTER_VALIDATION_REQUIRED",next.ServiceProvider.GetRequiredService<IAcmR57RuntimeGate>().Bloqueio(v));Assert.Equal(1,lookup.Calls);
    }
    static AcmR57NormalOptions Options()
    {
        var o=AutomaticFixture.Options() with{SapIdentifiersCopiedVerbatimApproved=false,SapContractCopiedVerbatimApproved=true,ApprovedMaterialMappings=[new(){Centro="3011",Item="10",SapMaterial="7000000",GuardianCode="7000000",EvidenceReference="SYNTHETIC_MATERIAL_PROOF"}]};
        Set(o,"DynamicEmitterValidationEnabled",true);Set(o,"DynamicEmitterSignatureId",133289);Set(o,"DynamicEmitterValidationFromUtc",(DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-1));return o;
    }
    static void Set(object value,string field,object? data){var p=value.GetType().GetProperty(field);Assert.NotNull(p);p.SetValue(value,data);}
    static void Stamp(VersaoContratoSapToledo v,DateTimeOffset at)=>Set(v,"RecebidoEmUtc",(DateTimeOffset?)at);
    static object Resolver(AcmR57NormalOptions o,Lookup lookup,TimeProvider? clock=null){var type=typeof(AcmR57NormalPreparation).Assembly.GetType("FugaTES.Aplicacao.Servicos.Mvp.AcmGuardianEmitterResolver");Assert.NotNull(type);return Activator.CreateInstance(type,o,lookup,clock??TimeProvider.System)!;}
    static IAcmR57RuntimeGate Gate(AcmR57NormalOptions o,object resolver)=>Assert.IsAssignableFrom<IAcmR57RuntimeGate>(Activator.CreateInstance(typeof(AcmR57NormalRuntimeGate),o,TimeProvider.System,resolver));
    static AcmR57Pipeline Pipeline(AutomaticFixture f,AcmR57NormalOptions o,object resolver)
    {var prep=Assert.IsAssignableFrom<IAcmR57Preparacao>(Activator.CreateInstance(typeof(AcmR57NormalPreparation),o,resolver));return new(f.Versions,prep,Gate(o,resolver),f.Soap,f.Rest,f.Journal,f.H4);}
    public sealed class Lookup:IGuardianEmitterRead
    {public int Calls;public string? Codigo;public GuardianEmitterReadResult Result=new(true,"20000169",1,133289,200,"R171_EMITTER_CONFIRMED");public Task<GuardianEmitterReadResult> ConsultarAsync(string codigo,CancellationToken ct){Calls++;Codigo=codigo;return Task.FromResult(Result);}}
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
}
