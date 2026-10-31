using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class AutomaticFlowTests
{
    [Fact]
    public async Task Reference_1147_creates_contract_and_protocol_without_weighing_or_tipo_operacao()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();
        var r=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal("R165U_CONFIRMED",r.Etapa);
        Assert.Equal("1147",f.Soap.Sent!.Codigo);Assert.Equal(135000.000m,f.Soap.Sent.ValorContrato);
        Assert.Equal("0020000051",f.Soap.Sent.CodigoEmissor);Assert.Equal("7000000",f.Soap.Sent.CodigoItem);
        Assert.Equal("308102",f.Soap.Sent.Estado);Assert.Equal("309837",f.Soap.Sent.TipoLancamento);
        Assert.Equal("OC1147",f.H4.Items[v.IntegracaoId].NumeroReferenciaSolicitado);
        Assert.Equal("1147",f.H4.Items[v.IntegracaoId].NumeroContratoSap);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
        Assert.Contains(f.Journal.History,x=>x.Etapa=="R165U_SOAP_CONFIRMED");
        Assert.Null(f.Rest.Sent!.Dados.CodigoFluxo);Assert.Null(f.Rest.Sent.Dados.CodigoTransportadora);
        Assert.Null(f.Rest.Sent.Dados.Documentos);Assert.Equal(0,f.Rest.Sent.Dados.IdAgenda);
        Assert.Null(f.H4.Items[v.IntegracaoId].CallbackIdGuardian);
    }

    [Fact]
    public async Task Two_distinct_contracts_are_processed_without_1106_scope()
    {
        var f=new AutomaticFixture();var(_,a)=f.Add("1147");
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,(await f.Pipeline().ExecutarAsync(a,default)).Status);
        var(_,b)=f.Add("1148");
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,(await f.Pipeline().ExecutarAsync(b,default)).Status);
        Assert.Equal(2,f.H4.Items.Count);Assert.Equal(2,f.Soap.Creates);Assert.Equal(2,f.Rest.Creates);
        Assert.Equal("1148",f.Soap.Sent!.Codigo);
    }

    [Fact]
    public async Task Replay_keeps_reference_and_protocol_without_any_new_create()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();
        var first=await f.Pipeline().ExecutarAsync(c,default);var link=f.H4.Items[v.IntegracaoId];
        var replay=c with{FencingToken=2,TentativaId=Guid.NewGuid()};f.Journal.Claims[c.IntegracaoId]=replay;
        var result=await f.Pipeline(AutomaticFixture.Options(false)).ExecutarAsync(replay,default);
        Assert.Equal(first,result);Assert.Equal(link,f.H4.Items[v.IntegracaoId]);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Soap.Lookups);Assert.Equal(1,f.Rest.Creates);
    }

    [Fact]
    public async Task Protocol_failure_does_not_discard_confirmed_contract_and_resume_never_recreates_it()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();
        var blocked=await f.Pipeline(AutomaticFixture.Options(false)).ExecutarAsync(c,default);
        Assert.Equal("R165U_PROTOCOL_BLOCKED",blocked.Etapa);Assert.Equal("1147",f.H4.Items[v.IntegracaoId].CodigoContratoConfirmado);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,(await f.Pipeline().ExecutarAsync(c,default)).Status);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Soap.Lookups);Assert.Equal(1,f.Rest.Creates);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unknown_or_contradictory_remote_existence_never_authorizes_create(bool contradiction)
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();
        f.Soap.Lookup=EstadoConsultaContratoGuardian.Indeterminado;
        if(contradiction){f.Soap.Lookup=EstadoConsultaContratoGuardian.Existente;f.Soap.Existing=null;}
        var r=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }

    [Fact]
    public async Task Existing_identical_contract_is_adopted_without_soap_write()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();
        f.Soap.Lookup=EstadoConsultaContratoGuardian.Existente;
        f.Soap.Existing=new AcmR57NormalPreparation(AutomaticFixture.Options()).PrepararContrato(v).Contrato;
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,(await f.Pipeline().ExecutarAsync(c,default)).Status);
        Assert.Equal(0,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
    }

    [Fact]
    public async Task Nonzero_soap_business_error_is_terminal_and_not_retried()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();f.Soap.Failure=new BusinessFailure();
        var r=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.FalhaNaoReprocessavel,r.Status);
        Assert.Equal(r,await f.Pipeline().ExecutarAsync(c,default));Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }

    private sealed class BusinessFailure:Exception,IAcmGuardianBusinessFailure { public int? BusinessErrorCode=>17; }

    [Theory]
    [InlineData("soap_timeout")] [InlineData("soap_wrong_identity")] [InlineData("soap_commit")]
    [InlineData("rest_timeout")] [InlineData("rest_wrong_identity")] [InlineData("rest_commit")]
    public async Task Uncertain_external_or_commit_outcome_is_armed_and_never_automatically_retried(string failure)
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();
        switch(failure)
        {
            case "soap_timeout":f.Soap.Failure=new TimeoutException();break;
            case "soap_wrong_identity":f.Soap.WrongCode="999";break;
            case "soap_commit":f.H4.FailContractCommit=true;break;
            case "rest_timeout":f.Rest.Failure=new TimeoutException();break;
            case "rest_wrong_identity":f.Rest.WrongIdentity=true;break;
            case "rest_commit":f.H4.FailProtocolCommit=true;break;
        }
        var result=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,result.Status);
        Assert.Equal(result,await f.Pipeline().ExecutarAsync(c,default));Assert.Equal(1,f.Soap.Creates);
        Assert.Equal(failure.StartsWith("rest")?1:0,f.Rest.Creates);
    }

    [Theory]
    [InlineData("partner")] [InlineData("material")] [InlineData("description")] [InlineData("quantity")]
    [InlineData("status")] [InlineData("weighing")]
    public async Task Missing_or_unapproved_contract_fields_block_before_external_lookup(string field)
    {
        var f=new AutomaticFixture();var(_,c)=f.Add(change:s=>field switch
        {"partner"=>s with{IdParceiro=null},"material"=>s with{Material=null},"description"=>s with{DescricaoTipoContrato=null},
         "quantity"=>s with{QuantidadeContrato=null},"status"=>s with{StatusContrato="X"},_=>s with{TipoPesagem="S"}});
        var r=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,r.Status);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Rest.Creates);
    }

    [Theory]
    [InlineData("stale")] [InlineData("worker")] [InlineData("expired")] [InlineData("commit_unknown")]
    public async Task Fencing_worker_lease_and_unknown_commit_block_all_external_writes(string reason)
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();
        if(reason=="stale")c=c with{FencingToken=2};
        if(reason=="worker")c=c with{Worker="OTHER"};
        if(reason=="expired"){c=c with{LeaseExpiraEm=DateTimeOffset.UtcNow.AddMinutes(-1)};f.Journal.Claims[c.IntegracaoId]=c;}
        if(reason=="commit_unknown")f.Journal.ThrowOnCommit=true;
        var r=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }

    [Fact]
    public async Task Journal_unavailable_prevents_lookup_and_create()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();f.Journal.Unavailable=true;
        Assert.Equal("R165U_DURABLE_JOURNAL_UNAVAILABLE",(await f.Pipeline().ExecutarAsync(c,default)).ErroCodigo);
        Assert.Equal(0,f.Soap.Lookups);
    }

    [Fact]
    public async Task Reference_collision_does_not_create_a_second_contract_or_protocol()
    {
        var f=new AutomaticFixture();var(_,a)=f.Add("1147");await f.Pipeline().ExecutarAsync(a,default);
        var(_,b)=f.Add("0000001147");var result=await f.Pipeline().ExecutarAsync(b,default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,result.Status);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
    }

    [Fact]
    public async Task Existing_protocol_without_matching_journal_requires_reconciliation_not_create()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add();var prep=new AcmR57NormalPreparation(AutomaticFixture.Options()).PrepararContrato(v);
        await f.H4.ReservarSolicitacaoUnicaAsync(v,prep.NumeroReferencia,prep.Contrato.Codigo,default);
        f.H4.Items[v.IntegracaoId]=f.H4.Items[v.IntegracaoId] with{CodigoContratoConfirmado="1147",ProtocoloIdGuardian=700};
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,(await f.Pipeline().ExecutarAsync(c,default)).Status);
        Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Rest.Creates);
    }

    [Fact]
    public async Task Required_protocol_payload_omits_carrier_flow_and_weighing_data()
    {
        var f=new AutomaticFixture();var(_,c)=f.Add();await f.Pipeline().ExecutarAsync(c,default);
        var payload=GuardianSet26HttpClient.CriarPayloadR57(f.Rest.Sent!);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data=json.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal("OC1147",data.GetProperty("numeroReferencia").GetString());Assert.Equal(0,data.GetProperty("idAgenda").GetInt32());
        Assert.False(data.TryGetProperty("codigoTransportadora",out _));Assert.Equal(JsonValueKind.Null,data.GetProperty("codigoFluxo").ValueKind);
        Assert.False(data.TryGetProperty("documentos",out _));Assert.False(data.TryGetProperty("tipoOperacaoSap",out _));
        Assert.Equal(3,data.GetProperty("camposAdicionais").GetArrayLength());
    }
}
