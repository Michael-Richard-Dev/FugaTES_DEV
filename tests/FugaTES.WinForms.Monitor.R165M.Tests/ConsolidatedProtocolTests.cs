using FugaTES.WinForms;
using FugaTES.WinForms.Monitor;
using System.Windows.Forms;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms.Guardian;
namespace FugaTES.Testes;
public sealed class ConsolidatedProtocolTests
{
    [Fact] public void Unified_session_exposes_protocol_screen_factory()
        => Assert.NotNull(typeof(UnifiedDesktopSession).GetMethod("CreateProtocols"));
    [Fact] public void Integrations_keeps_contract_json_and_adds_protocol_menu()
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            using var form=new FrmIntegracoes();
            var controls=GuardianProtocolFormTests.Descendants(form).ToArray();
            Assert.Contains(controls,c=>c.Name=="contratosJson");
            Assert.Contains(controls,c=>c.Name=="protocolosPreCadastro" && c.Text.Contains("Protocolos"));
        });
    }
    [Fact] public void Protocol_menu_requires_the_injected_unified_reader()
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            using var absent=new FrmIntegracoes();
            Assert.False(GuardianProtocolFormTests.Descendants(absent).Single(c=>c.Name=="protocolosPreCadastro").Enabled);
            using var bound=new FrmIntegracoes(protocolsRead:()=>throw new InvalidOperationException("Not clicked"));
            Assert.True(GuardianProtocolFormTests.Descendants(bound).Single(c=>c.Name=="protocolosPreCadastro").Enabled);
        });
    }
    [Fact] public async Task Unified_protocols_reuse_monitor_get_and_exact_detail_identity()
    {
        var id=Guid.NewGuid();var calls=new List<Guid>();var monitor=new ReadMonitor(new(true,true,[new(){IntegracaoId=id,Operacao="SAP_CONTRATO_TOLEDO",Documento="NEW-CONTRACT",EstadoMotor="RESULTADO_INDETERMINADO",CorrelationId="CORR"},new(){IntegracaoId=Guid.NewGuid(),Operacao="OTHER"}]));
        var adapter=new UnifiedProtocolReadRepository(monitor,(target,ct)=>
        {
            calls.Add(target);return Task.FromResult(new MonitorAcmDetalhe(new([],[],[]),new(){Contrato="NEW-CONTRACT",Referencia="OC-NEW",Protocolo="991"}));
        });
        var rows=await adapter.ConsultarAsync(default);
        var row=Assert.Single(rows);Assert.Equal(id,row.IntegracaoId);Assert.Equal("991",row.Protocolo);
        Assert.Equal([id],calls);Assert.Null(monitor.Filter!.Operacao);
        Assert.Null(row.StatusHttp);Assert.Null(row.PayloadEnviadoJson);Assert.False(ReenvioProtocolo.Avaliar(row).PodeExecutar);
    }
    [Fact] public async Task Failed_detail_keeps_other_rows_without_exposing_secrets()
    {
        var id=Guid.NewGuid();var other=Guid.NewGuid();var adapter=new UnifiedProtocolReadRepository(new ReadMonitor(new(true,true,[new(){IntegracaoId=id,Operacao="SAP_CONTRATO_TOLEDO"},new(){IntegracaoId=other,Operacao="SAP_CONTRATO_TOLEDO"}])),(target,_)=>target==id?throw new InvalidOperationException("Bearer secret must never be displayed"):Task.FromResult(new MonitorAcmDetalhe(new([],[],[]),new(){Contrato="OTHER"})));
        var rows=await adapter.ConsultarAsync(default);
        Assert.Equal(2,rows.Count);var incomplete=rows.Single(r=>r.IntegracaoId==id);
        Assert.Contains("PRECADASTRO_DETAIL_UNAVAILABLE",incomplete.UltimoErro);Assert.DoesNotContain("Bearer",incomplete.UltimoErro);
        Assert.True(incomplete.CorrelacaoAmbigua);Assert.Equal("OTHER",rows.Single(r=>r.IntegracaoId==other).Contrato);
    }
    [Fact] public async Task Global_read_limit_must_not_be_reported_as_no_protocols()
    {
        var rows=Enumerable.Range(0,500).Select(_=>new MonitorIntegracao{IntegracaoId=Guid.NewGuid(),Operacao="OTHER"}).ToArray();
        var adapter=new UnifiedProtocolReadRepository(new ReadMonitor(new(true,true,rows)),(_,_)=>throw new InvalidOperationException());
        var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>adapter.ConsultarAsync(default));
        Assert.Contains("PRECADASTRO_DEV_READ_LIMIT_REACHED",failure.Message);
    }
    [Fact] public void Contract_navigation_has_an_exact_integration_scope()
        => Assert.Contains(typeof(FrmContratosAcm).GetConstructors().Single().GetParameters(),p=>p.Name=="integrationScope");
    [Fact] public async Task Cancelled_read_never_becomes_a_successful_empty_list()
    {
        using var stop=new CancellationTokenSource();stop.Cancel();
        var adapter=new UnifiedProtocolReadRepository(new ReadMonitor(new(true,true,[])),(_,_)=>throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>adapter.ConsultarAsync(stop.Token));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Contract_navigation_never_substitutes_another_integration(bool missing)
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            var selected=Guid.NewGuid();var rows=new[] {new ContratoAcmLeitura("3011","A",1,DateTime.UtcNow,"T","FALHA_DEFINITIVA",selected,"A",true),new ContratoAcmLeitura("3011","B",1,DateTime.UtcNow,"T","FALHA_DEFINITIVA",Guid.NewGuid(),"B",true)};
            using var form=new FrmContratosAcm(new ContratosAcmConsulta(new ContractRows(rows)),integrationScope:missing?Guid.NewGuid():selected);
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new(-20000,-20000);form.Show();Application.DoEvents();
            var grid=GuardianProtocolFormTests.Descendants(form).OfType<DataGridView>().Single();
            Assert.Equal(missing?0:1,grid.Rows.Count);
            if(!missing)Assert.Equal(selected,((ContratoAcmLeitura)grid.Rows[0].DataBoundItem!).IntegracaoId);
            Assert.True(grid.Columns.Contains("json"));Assert.True(grid.Columns.Contains("reenviar"));form.Close();
        });
    }
    sealed class ContractRows(IReadOnlyList<ContratoAcmLeitura> rows):IContratosAcmReadRepository
    {
        public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro,CancellationToken ct)=>Task.FromResult(rows);
    }
    sealed class ReadMonitor(MonitorSnapshot snapshot):IMonitorReadRepository
    {
        public MonitorFiltro? Filter;
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filter,CancellationToken ct){ct.ThrowIfCancellationRequested();Filter=filter;return Task.FromResult(snapshot);}
        public Task<MonitorDetalhe> DetalharAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException("Use exact ACM readback");
    }
}
