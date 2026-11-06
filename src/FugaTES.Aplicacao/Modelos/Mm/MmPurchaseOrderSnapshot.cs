namespace FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Configuracao;
public static class MmCollectionPolicy
{
    public const string SapOrigin="https://vhfufds4ci.sap.fugacouros.com.br:44300";
    public static readonly TimeSpan Interval=TimeSpan.FromMinutes(3);
    public static readonly DateOnly CreationCutoff=new(2026,11,1);
    public static bool Eligible(PedidoCompraMmLeitura row)=>row.DataCriacao is {} date&&date>=CreationCutoff;
}
public interface IMmSnapshotStore
{
    MmPurchaseOrderSnapshot Read();
    void Write(MmPurchaseOrderSnapshot value);
    void Update(Func<MmPurchaseOrderSnapshot,MmPurchaseOrderSnapshot> update);
}
public record MmPurchaseOrderSnapshot
{
    public int Schema {get;init;}=1;
    public string RuntimeMode {get;init;}="WINDOWS";
    public string Host {get;init;}="";
    public int IntervalSeconds {get;init;}=180;
    public DateOnly CreationCutoff {get;init;}=MmCollectionPolicy.CreationCutoff;
    public DateTimeOffset? RegistryReadUtc {get;init;}
    /// <summary>
    /// De qual SAP estes pedidos foram lidos. PENDENTE: ninguem atribui estes dois campos - eles
    /// ficam no padrao DEV110 abaixo, que e verdade enquanto so o DEV110 coleta. Um coletor de
    /// QAS120 gravaria um cache rotulado DEV110, e o rotulo e justamente o que distingue os dois
    /// arquivos. Quem construir o snapshot (MmPurchaseOrderCollector) tem de passar o ambiente
    /// que de fato leu; a guarda do cache ja aceita qualquer ambiente aprovado.
    /// </summary>
    public string Origin {get;init;}=MmCollectionPolicy.SapOrigin;
    public string Client {get;init;}=AmbientesSap.Dev110.SapClient;
    public string Company {get;init;}="3000";
    public string[] Plants {get;init;}=["3011","3008"];
    public DateTimeOffset? LastAttemptUtc {get;init;}
    public DateTimeOffset? LastSuccessUtc {get;init;}
    public DateTimeOffset? NextPollUtc {get;init;}
    public string State {get;init;}="MM_NOT_STARTED";
    public int? Http {get;init;}
    public int Pages {get;init;}
    public int Cycles {get;init;}
    public int NewItems {get;init;}
    public int ChangedItems {get;init;}
    public IReadOnlyList<PedidoCompraMmLeitura> Items {get;init;}=Array.Empty<PedidoCompraMmLeitura>();
    public string GuardianState {get;init;}="MM_GUARDIAN_DESATIVADO";
    public string? GuardianCallback {get;init;}
    public long GuardianRevision {get;init;}
    public IReadOnlyList<RegistroProtocoloMm> GuardianRecords {get;init;}=Array.Empty<RegistroProtocoloMm>();
    public bool IsFresh(DateTimeOffset now)=>LastSuccessUtc is {} success&&success<=now.AddSeconds(5)&&now-success<TimeSpan.FromSeconds(IntervalSeconds+90)&&State is "MM_SAP_COMPLETE" or "MM_COLLECTING";
}


public sealed record MmRemoteReadback(MmPurchaseOrderSnapshot Snapshot,string Provenance,DateTimeOffset ReadAtUtc,bool Fresh);
