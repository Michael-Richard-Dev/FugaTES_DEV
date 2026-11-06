using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms;
using FugaTES.WinForms.Mm;
namespace R158.SapInboundReadiness.Tests;
public class MmGuardianMonitorTests
{
    [Fact] public async Task Screen_has_both_original_sap_and_guardian_json_columns()
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{using var form=new FrmPedidosCompraMm(()=>new MmPurchaseOrderSnapshot(),()=>{});var grid=Find(form).OfType<DataGridView>().Single();Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.Name=="jsonMm");Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.Name=="jsonGuardianMm");Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.DataPropertyName=="ProtocoloGuardian");completion.SetResult();}catch(Exception ex){completion.SetException(ex);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static IEnumerable<Control> Find(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(var c in Find(child))yield return c;}}
}

public class MmGuardianProjectionTests
{
    [Fact] public void Stored_json_is_used_even_when_current_source_changed()
    {
        var line=MmGuardianProtocolTests.Linha();var json=FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.Serializar(FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.Montar(line,"https://example.invalid/mm"));
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        var record=new RegistroProtocoloMm(Guid.NewGuid(),"3000/4500002895/10","4500002895","10","3011",json,hash,"CRIADO",201,123,"safe",DateTimeOffset.UtcNow,FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.SourceHash(line));
        var row=new MmGuardianMonitorRow(line with{Material="7000002"},record,"https://example.invalid/new-mm","MM_GUARDIAN_VERIFICADO");
        Assert.Equal(json,row.JsonGuardian);Assert.Equal("123",row.ProtocoloGuardian);Assert.Contains("ALTERADO_APOS_ENVIO",row.EstadoGuardian);
    }
    [Fact] public void Changed_quantity_is_signaled_even_when_guardian_payload_has_not_changed()
    {
        var line=MmGuardianProtocolTests.Linha();
        var request=FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.Montar(line,"https://example.invalid/mm");
        var json=FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.Serializar(request);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        var record=new RegistroProtocoloMm(Guid.NewGuid(),"3000/4500002895/10",line.Pedido,line.Item,line.Centro,json,hash,"CRIADO",201,123,"safe",DateTimeOffset.UtcNow,FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.SourceHash(line));
        var row=new MmGuardianMonitorRow(line with{Quantidade=line.Quantidade+1},record,"https://example.invalid/mm","MM_GUARDIAN_VERIFICADO");
        Assert.Equal(json,row.JsonGuardian);Assert.Contains("ALTERADO_APOS_ENVIO",row.EstadoGuardian);
    }
    [Fact] public void Missing_callback_does_not_fabricate_a_sent_json()
    {var row=new MmGuardianMonitorRow(MmGuardianProtocolTests.Linha(),null,null,"MM_GUARDIAN_DESATIVADO");Assert.Null(row.JsonGuardian);Assert.Equal("MM_GUARDIAN_DESATIVADO",row.EstadoGuardian);}
}

public class MmGuardianReviewRegressionTests
{
    [Fact] public void Invalid_material_shows_a_specific_block_instead_of_waiting()
    {var row=new MmGuardianMonitorRow(MmGuardianProtocolTests.Linha() with{Material=""},null,"https://example.invalid/mm","MM_GUARDIAN_VERIFICADO");Assert.Contains("BLOQUEADO_DADOS",row.EstadoGuardian);Assert.False(string.IsNullOrWhiteSpace(row.MensagemGuardian));}
    [Fact] public void Registry_keeps_source_fingerprint_independent_from_guardian_payload()
        =>Assert.NotNull(typeof(RegistroProtocoloMm).GetProperty("SourceSha256"));
}
