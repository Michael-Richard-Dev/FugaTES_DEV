using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using FugaTES.Persistencia.Transportadoras.Fakes;
namespace FugaTES.DurableSnapshot.R24.Tests;
public class ContractFocalFixR25DTests
{
    [Fact] public async Task UNAPPROVED_TEMPORAL_FAIL_CLOSED()
    {
        var scope=new EscopoSnapshotR24(Guid.NewGuid(),"SAP_HML","110","R20A_V1","");
        var c=new ContextoSnapshotR24(scope with{ScopeFingerprint=SnapshotFingerprintR24.Scope(scope)},VersoesSnapshotR24.PropostaAtual);
        var now=new DateTimeOffset(2026,10,11,12,0,0,TimeSpan.Zero);
        var row=new RegistroBpSnapshotR24("0001",AvaliacaoCrm010R24.INDETERMINATE,now.AddDays(-30),now.AddDays(30),null,true);
        row=row with{RowFingerprint=SnapshotFingerprintR24.Row(c.Versoes,row)};
        var page=new PaginaSnapshotR24(0,0,1,1,true,[row],"");page=page with{PageFingerprint=SnapshotFingerprintR24.Page(page)};
        var repo=new SnapshotMasterdataMemoryFakeR24(new());var id=Guid.NewGuid();
        await repo.IniciarTentativaAsync(new(c,id,now),default);
        await repo.RegistrarLoteAsync(new(c,id,ModoLoteSnapshotR24.APPEND_PAGE,page,[]),default);
        var hash=SnapshotFingerprintR24.Snapshot(c,now,1,[row]);
        var ready=await repo.ValidarCompletudeAsync(new(c,id,1,hash),default);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,ready.Situacao);
        Assert.Equal("TEMPORAL_POLICY_NOT_APPROVED",ready.Codigo);
        Assert.Null((await repo.PromoverSnapshotAsync(new(c,id,PonteiroSnapshotR24.Vazio,hash),default)).Dados);
    }

    static readonly DateTimeOffset Now=new(2026,10,11,12,0,0,TimeSpan.Zero);
    static ContextoSnapshotR24 Context()
    {
        var s=new EscopoSnapshotR24(Guid.NewGuid(),"SAP_HML","110","R20A_V1","");
        return new(s with{ScopeFingerprint=SnapshotFingerprintR24.Scope(s)},VersoesSnapshotR24.PropostaAtual);
    }
    static RegistroBpSnapshotR24 Row(string bp="0001",string? name=null,int extraTicks=0)
    {
        var r=new RegistroBpSnapshotR24(bp,AvaliacaoCrm010R24.EVALUATED_VALID,Now.AddDays(-30).AddTicks(extraTicks),Now.AddDays(30),name,true);
        return r with{RowFingerprint=SnapshotFingerprintR24.Row(VersoesSnapshotR24.PropostaAtual,r)};
    }
    sealed class Clock:TimeProvider {public DateTimeOffset Value=Now.AddHours(1).ToOffset(TimeSpan.FromHours(-3));public override DateTimeOffset GetUtcNow()=>Value;}
    sealed class Harness
    {
        public readonly ContextoSnapshotR24 C=Context();public readonly SnapshotMemoryStorageR24 Storage=new();
        public readonly Clock Time=new();public readonly SnapshotMasterdataMemoryFakeR24 Repo;
        public readonly Guid Id=Guid.NewGuid();public string Hash="";
        public Harness()=>Repo=new(Storage,Time,ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(C.Escopo.ScopeId));
        public async Task Ready()
        {
            await Repo.IniciarTentativaAsync(new(C,Id,Now),default);
            var r=Row();var p=new PaginaSnapshotR24(0,0,1,1,true,[r],"");p=p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};
            await Repo.RegistrarLoteAsync(new(C,Id,ModoLoteSnapshotR24.APPEND_PAGE,p,[]),default);
            Hash=SnapshotFingerprintR24.Snapshot(C,Now,1,[r]);
            Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await Repo.ValidarCompletudeAsync(new(C,Id,1,Hash),default)).Situacao);
        }
        public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> Promote()=>Repo.PromoverSnapshotAsync(new(C,Id,PonteiroSnapshotR24.Vazio,Hash),default);
    }
    [Fact] public async Task CONSOLIDATED_AT_UTC_INJECTED()
    {
        var h=new Harness();await h.Ready();var validated=h.Time.Value.ToUniversalTime();h.Time.Value=Now.AddHours(2).ToOffset(TimeSpan.FromHours(4));
        h.Storage.AntesDoProximoCommitDePromocao(h.C.Escopo.ScopeId,()=>h.Time.Value=Now.AddHours(3).ToOffset(TimeSpan.FromHours(-2)));
        var s=(await h.Promote()).Dados!;Assert.Equal(Now.AddHours(3),s.ConsolidatedAt);Assert.Equal(TimeSpan.Zero,s.ConsolidatedAt.Offset);
        Assert.Equal(validated,s.ValidatedAt);Assert.Equal(TimeSpan.Zero,s.ValidatedAt.Offset);Assert.NotEqual(s.ReferenceAt,s.ConsolidatedAt);
    }
    [Fact] public async Task REFERENCE_AT_UNCHANGED()
    {
        var h=new Harness();await h.Ready();h.Time.Value=Now.AddHours(2);var s=(await h.Promote()).Dados!;
        Assert.Equal(Now,s.ReferenceAt);Assert.Equal(h.Hash,s.SnapshotFingerprint);
        Assert.Equal(h.Hash,SnapshotFingerprintR24.Snapshot(h.C,s.ReferenceAt,s.PageCount,s.Rows));
    }
    [Fact] public async Task IDEMPOTENT_REPLAY_PRESERVES_TIME()
    {
        var h=new Harness();await h.Ready();var first=(await h.Promote()).Dados!;h.Time.Value=Now.AddDays(1);
        var replay=await h.Promote();Assert.Equal(SituacaoSnapshotR24.REPLAY,replay.Situacao);Assert.Equal(first,replay.Dados);
        Assert.Equal(first.ConsolidatedAt,replay.Dados!.ConsolidatedAt);Assert.Equal(first.ValidatedAt,replay.Dados.ValidatedAt);
    }
    [Fact] public async Task FAILED_PROMOTION_HAS_NO_CONSOLIDATION()
    {
        var h=new Harness();await h.Ready();h.Storage.FalharProximaPromocao(h.C.Escopo.ScopeId,FalhaPromocaoFakeR24.BeforeCommit);
        Assert.Null((await h.Promote()).Dados);Assert.Null((await h.Repo.CarregarSnapshotAtivoAsync(new(h.C),default)).Dados);
    }
    [Fact] public async Task CLOCK_REGRESSION_FAIL_CLOSED()
    {
        var h=new Harness();await h.Ready();h.Time.Value=Now;Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Promote()).Situacao);
        Assert.Null((await h.Repo.CarregarSnapshotAtivoAsync(new(h.C),default)).Dados);
    }
    [Fact] public async Task SYNTHETIC_TEMPORAL_ISOLATION()
    {
        var h=new Harness();await h.Ready();Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await h.Promote()).Situacao);
        var normal=new SnapshotMasterdataMemoryFakeR24(h.Storage,h.Time);
        Assert.Null((await normal.CarregarSnapshotAtivoAsync(new(h.C),default)).Dados);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await normal.PromoverSnapshotAsync(new(h.C,h.Id,PonteiroSnapshotR24.Vazio,h.Hash),default)).Situacao);
        Assert.Empty(typeof(ContextoTemporalSinteticoSnapshotR25D).GetConstructors());
    }
    [Fact] public async Task SYNTHETIC_SCOPE_MISMATCH_BLOCKED()
    {
        var h=new Harness();await h.Ready();var wrong=new SnapshotMasterdataMemoryFakeR24(h.Storage,h.Time,ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(Guid.NewGuid()));
        Assert.Null((await wrong.PromoverSnapshotAsync(new(h.C,h.Id,PonteiroSnapshotR24.Vazio,h.Hash),default)).Dados);
    }
    [Fact] public void SYNTHETIC_UNKNOWN_IDENTITY_INDETERMINATE()
    {
        var p=ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(Guid.NewGuid());
        Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,p.Evaluate("UNKNOWN_BP",Now.AddDays(-1),Now.AddDays(1),Now));
        Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,p.Evaluate("0001",Now,Now.AddDays(1),Now));
    }
    [Theory][InlineData(0,false)][InlineData(1,true)][InlineData(9,true)][InlineData(10,false)]
    public void TIMESTAMP_PRECISION_GUARD(int extra,bool blocked)
    {
        var instant=Now.AddTicks(extra).ToOffset(TimeSpan.FromHours(-3));var result=SnapshotTemporalPrecisionR25D.Propose(instant);
        Assert.Equal(blocked,result.PrecisionLossBlocked);Assert.Equal(instant.UtcTicks,result.CanonicalUtcTicks);
        Assert.Equal(instant,SnapshotTemporalPrecisionR25D.RestoreCanonicalTicks(result.CanonicalUtcTicks));Assert.False(result.SqlRoundTripProven);
        if(blocked){Assert.Null(result.OperationalMicrosecondInstant);Assert.Equal("PRECISION_LOSS_BLOCKED",result.Code);}
        else Assert.Equal(TimeSpan.Zero,result.OperationalMicrosecondInstant!.Value.Offset);
    }
    static readonly Dictionary<string,(string Hex,string Hash)> Golden=new()
    {
        ["ROW_NULL"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000003524F570000000101000000165232345F42494E4152595F50524F504F53414C5F5631010000001D5232345F45584143545F494E5354414E545F50524F504F53414C5F5631010000000430303031000000010108DF0FFC34AFA0000108DF3F222578A00000","d62019bca2077f85b617bdb8e71456f7b0b71031f3c0b21190d4329aaaaaedf9"),
        ["ROW_EMPTY"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000003524F570000000101000000165232345F42494E4152595F50524F504F53414C5F5631010000001D5232345F45584143545F494E5354414E545F50524F504F53414C5F5631010000000430303031000000010108DF0FFC34AFA0000108DF3F222578A0000100000000","d8dd7ab71067e263248394072bebb2cfea20703e4b61134ffb2eba8222522d37"),
        ["ROW_MULTIBYTE"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000003524F570000000101000000165232345F42494E4152595F50524F504F53414C5F5631010000001D5232345F45584143545F494E5354414E545F50524F504F53414C5F5631010000000430303032000000010108DF0FFC34AFA0000108DF3F222578A00001000000124F46464C494E455F61C3A7C3A36FF09F8CB1","28148dbc9c282386a56ff1898e02c1df5ba4f6066e22d716f721de1bad8246ee"),
        ["ROW_NO_LEADING_ZERO"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000003524F570000000101000000165232345F42494E4152595F50524F504F53414C5F5631010000001D5232345F45584143545F494E5354414E545F50524F504F53414C5F5631010000000131000000010108DF0FFC34AFA0000108DF3F222578A00000","da310fb579be50ebc2bb027640f15ba66cfd960932983ce742afb036b8cec5f2"),
        ["ROW_SUBMICRO"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000003524F570000000101000000165232345F42494E4152595F50524F504F53414C5F5631010000001D5232345F45584143545F494E5354414E545F50524F504F53414C5F5631010000000430303031000000010108DF0FFC34AFA0010108DF3F222578A00000","e61b5e06b67e26d7a0085b384b37714c0184efdb1914186224e41ac8422d3e6a"),
        ["SCOPE"]=("01000000165232345F42494E4152595F50524F504F53414C5F5631010000000553434F504501000000075341505F484D4C01000000033131300100000015415F427573696E657373506172746E6572526F6C65010000000643524D3031300100000007523230415F5631010000001643524D3031305F43454E5445525F44495341424C45440100000014425553494E4553535F504152544E45525F4153430100000008544F505F534B4950","f0043207b9c6ba6f930bfabf1c6c666fdf47bf6e3d0450fbc2761de2278aee49"),
        ["PAGE"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000004504147450000000000000000000000000000000200000002000000010100000004303030310108DF0FFC34AFA0000108DF3F222578A0000100000004303030320108DF0FFC34AFA0000108DF3F222578A000","e5442a043a7603723d35ad67dc5eca42f00ae7a64643bcb271d5f83884b4ed26"),
        ["SNAPSHOT"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000008534E415053484F540100000040663030343332303762396336626136663933306266616266316336633636366664663437626636653364303435306662633237363164653232373861656534390000000101000000165232345F42494E4152595F50524F504F53414C5F5631010000001D5232345F45584143545F494E5354414E545F50524F504F53414C5F56310108DF278F2D1420000000000100000002010000000430303031010000004064363230313962636132303737663835623631376264623865373134353666376230623731303331663363306232313139306434333239616161616165646639010000000430303032010000004032383134386462633963323832333836613536666631383938653032633164663562613466363036366532326437313666373231646531626164383234366565","49ba4e9ea9a79a192a2840c6f8dda2a44cd2a25903bd05b0f2f9644e4087b804"),
        ["ROW_R25T"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000003524F570000000201000000165232345F42494E4152595F50524F504F53414C5F56310100000032523235545F425553494E4553535F444154455F494E434C55534956455F4D49444E494748545F4558434C55534956455F5631010000000430303031000000010108DF0FFC34AFA0000108DF3F222578A00000","b8cbb29e6ded3ebce3b40edb4e5878425c550ada38dcf7e43697333725c1eb6e"),
        ["SNAPSHOT_R25T"]=("01000000165232345F42494E4152595F50524F504F53414C5F56310100000008534E415053484F540100000040663030343332303762396336626136663933306266616266316336633636366664663437626636653364303435306662633237363164653232373861656534390000000201000000165232345F42494E4152595F50524F504F53414C5F56310100000032523235545F425553494E4553535F444154455F494E434C55534956455F4D49444E494748545F4558434C55534956455F56310108DF278F2D1420000000000100000002010000000430303031010000004062386362623239653664656433656263653362343065646234653538373834323563353530616461333864636637653433363937333333373235633165623665010000000430303032010000004065613864393866623563643730383261343037306665653266303538333464353561356437343839366138396531393736323963363932353937343430656164","9d15917ec2f9e8968f1f66cb7b8e92a6e07e505286427b88e3af8232752d8fcd"),
    };
    static Dictionary<string,byte[]> Actual()
    {
        var c=Context();var rows=new[]{Row(),Row("0002","OFFLINE_ação🌱")};
        var p=new PaginaSnapshotR24(0,0,2,2,true,rows.Reverse().ToImmutableArray(),"");
        return new()
        {
            ["ROW_NULL"]=SnapshotFingerprintR24.CanonicalRow(c.Versoes,rows[0]),
            ["ROW_EMPTY"]=SnapshotFingerprintR24.CanonicalRow(c.Versoes,Row(name:"")),
            ["ROW_MULTIBYTE"]=SnapshotFingerprintR24.CanonicalRow(c.Versoes,rows[1]),
            ["ROW_NO_LEADING_ZERO"]=SnapshotFingerprintR24.CanonicalRow(c.Versoes,Row("1")),
            ["ROW_SUBMICRO"]=SnapshotFingerprintR24.CanonicalRow(c.Versoes,Row(extraTicks:1)),
            ["SCOPE"]=SnapshotFingerprintR24.CanonicalScope(c.Escopo),
            ["PAGE"]=SnapshotFingerprintR24.CanonicalPage(p),
            ["SNAPSHOT"]=SnapshotFingerprintR24.CanonicalSnapshot(c,Now,1,rows.Reverse()),
            ["ROW_R25T"]=SnapshotFingerprintR24.CanonicalRow(VersoesSnapshotR24.AprovadaR25T,rows[0]),
            ["SNAPSHOT_R25T"]=SnapshotFingerprintR24.CanonicalSnapshot(c with{Versoes=VersoesSnapshotR24.AprovadaR25T},Now,1,rows.Reverse())
        };
    }
    [Fact] public void FOUR_DOMAIN_GOLDEN_VECTORS()
    {
        var actual=Actual();foreach(var (key,bytes) in actual)
        {
            Assert.Equal(Golden[key].Hex,Convert.ToHexString(bytes));Assert.Equal(Golden[key].Hash,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        if(Environment.GetEnvironmentVariable("FUGATES_OFFLINE_VECTOR_OUTPUT") is {} path)
            File.WriteAllText(path,JsonSerializer.Serialize(actual.Select(x=>new{Id=x.Key,Hex=Convert.ToHexString(x.Value),Length=x.Value.Length,SHA256=Convert.ToHexString(SHA256.HashData(x.Value)).ToLowerInvariant()}),new JsonSerializerOptions{WriteIndented=true}));
    }
    [Theory][InlineData("pt-BR")][InlineData("en-US")]
    public void CULTURE_INVARIANCE(string culture)
    {
        var old=CultureInfo.CurrentCulture;var ui=CultureInfo.CurrentUICulture;
        try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(culture);
            foreach(var (key,bytes) in Actual())Assert.Equal(Golden[key].Hex,Convert.ToHexString(bytes));}
        finally{CultureInfo.CurrentCulture=old;CultureInfo.CurrentUICulture=ui;}
    }
    [Fact] public void MULTIBYTE_AND_NULL_EMPTY()
    {
        var a=Actual();Assert.NotEqual(a["ROW_NULL"],a["ROW_EMPTY"]);Assert.NotEqual(a["ROW_NULL"],a["ROW_NO_LEADING_ZERO"]);
        Assert.Equal(Golden["ROW_MULTIBYTE"].Hex,Convert.ToHexString(a["ROW_MULTIBYTE"]));
    }
    [Fact] public void REVERSED_INPUT_ORDER()
    {
        var c=Context();var rows=new[]{Row(),Row("0002","OFFLINE_ação🌱")};
        Assert.Equal(SnapshotFingerprintR24.CanonicalSnapshot(c,Now,1,rows),SnapshotFingerprintR24.CanonicalSnapshot(c,Now,1,rows.Reverse()));
        var p=new PaginaSnapshotR24(0,0,2,2,true,rows.ToImmutableArray(),"");
        Assert.Equal(SnapshotFingerprintR24.CanonicalPage(p),SnapshotFingerprintR24.CanonicalPage(p with{Rows=rows.Reverse().ToImmutableArray()}));
    }
    [Fact] public void UTC_OFFSET_EQUIVALENCE()
    {
        var c=Context();var row=Row(extraTicks:1);var offset=row with{RoleValidFromObserved=row.RoleValidFromObserved!.Value.ToOffset(TimeSpan.FromHours(-3)),RoleValidToObserved=row.RoleValidToObserved!.Value.ToOffset(TimeSpan.FromHours(4))};
        Assert.Equal(SnapshotFingerprintR24.CanonicalRow(c.Versoes,row),SnapshotFingerprintR24.CanonicalRow(c.Versoes,offset));
        Assert.Equal(SnapshotFingerprintR24.CanonicalSnapshot(c,Now,1,[row]),SnapshotFingerprintR24.CanonicalSnapshot(c,Now.ToOffset(TimeSpan.FromHours(-3)),1,[offset]));
    }
    [Fact] public void SCHEMA_VERSION_MISMATCH()
    {
        var c=Context();var v=c.Versoes with{Contrato=2};
        Assert.Throws<NotSupportedException>(()=>SnapshotFingerprintR24.CanonicalRow(v,Row()));
        Assert.Throws<NotSupportedException>(()=>SnapshotFingerprintR24.CanonicalSnapshot(c with{Versoes=v},Now,1,[Row()]));
    }
    sealed class Pages(SapRoleRowR20A row):ISapRolePageReaderR20A {public Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r,CancellationToken ct)=>Task.FromResult(new SapRolePageR20A(r.Skip==0?[row]:[]));}
    sealed class Bps:ISapBpMasterReaderR20B
    {public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)=>Task.FromResult(new SapBpReadResultR20B(bp,JsonSerializer.SerializeToElement(new{BusinessPartner=bp,BusinessPartnerFullName="OFFLINE_NAME",SecretTaxData="OFFLINE_TAX_DO_NOT_STORE",Email="offline@example.invalid"})));}
    static async Task<SapBpEnrichedSnapshotR20B> Enriched()
    {
        var role=new SapRoleRowR20A("0000000042","CRM010","/Date("+Now.AddDays(-30).ToUnixTimeMilliseconds()+")/","/Date("+Now.AddDays(30).ToUnixTimeMilliseconds()+")/");
        var clock=new Clock();var roles=await new SapRolePagedEnumeratorR20A(new Pages(role),clock).EnumerateAsync(new(10,10,100));
        var policy=new SapBpComparisonPolicyR20B([SapBpFieldR20B.BusinessPartner,SapBpFieldR20B.BusinessPartnerFullName],true,true,"OFFLINE_APPROVED",temporalContract:SapRoleTemporalContractR20B.EdmDateTimeOffset);
        return await new SapBpSnapshotEnricherR20B(new Bps(),clock).EnrichAsync(roles,policy);
    }
    [Fact] public async Task UNAPPROVED_PROJECTION_INDETERMINATE()
    {Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,Assert.Single(SapSnapshotWhitelistProjectionR24.Project(await Enriched(),versions:VersoesSnapshotR24.PropostaAtual)).Crm010TemporalEvaluation);}
    [Fact] public async Task NO_SENSITIVE_FIELD_RETENTION()
    {
        var enriched=await Enriched();var rows=SapSnapshotWhitelistProjectionR24.Project(enriched);var body=JsonSerializer.Serialize(rows);
        Assert.Null(Assert.Single(rows).ApprovedFullNameNormalized);Assert.DoesNotContain("OFFLINE_TAX_DO_NOT_STORE",body);Assert.DoesNotContain("offline@example.invalid",body);
        Assert.Throws<InvalidOperationException>(()=>SapSnapshotWhitelistProjectionR24.Project(enriched,true,"ARBITRARY_APPROVAL"));
        Assert.Throws<InvalidOperationException>(()=>SapSnapshotWhitelistProjectionR24.Project(enriched,true,"OFFLINE_RETENTION_APPROVED"));
    }
    [Fact] public async Task FAKE_REJECTS_NAME_WITHOUT_CAPABILITY()
    {
        var c=Context();var repo=new SnapshotMasterdataMemoryFakeR24(new());var id=Guid.NewGuid();await repo.IniciarTentativaAsync(new(c,id,Now),default);
        var r=Row(name:"OFFLINE_NAME");var p=new PaginaSnapshotR24(0,0,1,1,true,[r],"");p=p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await repo.RegistrarLoteAsync(new(c,id,ModoLoteSnapshotR24.APPEND_PAGE,p,[]),default)).Situacao);
    }
    [Fact] public async Task UNAPPROVED_DEFINITIVE_CLAIM_REJECTED()
    {
        var c=Context();var repo=new SnapshotMasterdataMemoryFakeR24(new());var id=Guid.NewGuid();await repo.IniciarTentativaAsync(new(c,id,Now),default);
        var r=Row();var p=new PaginaSnapshotR24(0,0,1,1,true,[r],"");p=p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};
        var result=await repo.RegistrarLoteAsync(new(c,id,ModoLoteSnapshotR24.APPEND_PAGE,p,[]),default);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,result.Situacao);Assert.Equal("TEMPORAL_POLICY_NOT_APPROVED",result.Codigo);
        Assert.Null((await repo.CarregarSnapshotAtivoAsync(new(c),default)).Dados);
    }
    [Fact] public async Task DIRECT_STORE_NONNULL_NAME_REJECTED_WITH_SYNTHETIC_CAPABILITY()
    {
        var h=new Harness();var r=Row(name:"OFFLINE_NAME");var p=new PaginaSnapshotR24(0,0,1,1,true,[r],"");p=p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};
        await h.Repo.IniciarTentativaAsync(new(h.C,h.Id,Now),default);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.RegistrarLoteAsync(new(h.C,h.Id,ModoLoteSnapshotR24.APPEND_PAGE,p,[]),default)).Situacao);
    }
    [Fact] public async Task CRM010_PROJECTION_USES_APPROVED_VERSION()
    {
        var rows=SapSnapshotWhitelistProjectionR24.Project(await Enriched());var row=Assert.Single(rows);
        Assert.Equal(AvaliacaoCrm010R24.EVALUATED_VALID,row.Crm010TemporalEvaluation);
        Assert.Equal(SnapshotFingerprintR24.Row(VersoesSnapshotR24.AprovadaR25T,row),row.RowFingerprint);
        Assert.NotEqual(SnapshotFingerprintR24.Row(VersoesSnapshotR24.PropostaAtual,row),row.RowFingerprint);
    }
}
