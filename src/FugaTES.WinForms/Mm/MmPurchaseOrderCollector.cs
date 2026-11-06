using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.WinForms.Mm;
public sealed class MmPurchaseOrderCollector : FugaTES.Aplicacao.Servicos.Mm.MmPurchaseOrderCollector
{
    public MmPurchaseOrderCollector(IPedidosCompraMmConsulta source,MmSnapshotStore store):base(source,store){}
    public MmPurchaseOrderCollector(IPedidosCompraMmConsulta source,MmSnapshotStore store,IMmGuardianDispatcher? guardian):base(source,store,guardian){}
}
