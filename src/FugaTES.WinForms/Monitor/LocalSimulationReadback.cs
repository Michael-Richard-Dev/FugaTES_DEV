using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;

namespace FugaTES.WinForms.Monitor;

// Explicit demo-only source. Never uses the corporate profile or a command transport.
public sealed class LocalSimulationReadback(string path, Guid runId) : IMonitorReadRepository, IGuardianProtocolClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Configured => false;
    public bool PreviewAvailable => true;
    public string PreviewSource => "SIMULACAO LOCAL R165AJ - READBACK DO HARNESS";
    public string LockReason => "SOMENTE LEITURA - MOTOR AUTOMATICO; ENVIO MANUAL BLOQUEADO";
    public string Estado => "SIMULACAO LOCAL - H4/journal em memoria, sem PostgreSQL ou Guardian real";

    private LocalSimulationDocument Read()
    {
        if (!Path.IsPathFullyQualified(path) || runId == Guid.Empty) throw new InvalidOperationException("LOCAL_DEMO_IDENTITY_REQUIRED");
        var file=new FileInfo(path);
        if (!file.Exists || file.Length is <2 or >1048576 || (file.Attributes & FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("LOCAL_DEMO_EXPORT_UNAVAILABLE");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var doc=JsonSerializer.Deserialize<LocalSimulationDocument>(input,Json) ?? throw new InvalidOperationException();
        var age=DateTimeOffset.UtcNow-doc.CapturedUtc;
        if (doc.Schema!="R165AJ_LOCAL_SIMULATION_V1" || doc.RunId!=runId || !doc.Simulated || age>TimeSpan.FromSeconds(15)
            || age<TimeSpan.FromSeconds(-5) || doc.Scenarios.Length>3
            || doc.Scenarios.Any(x=>x.Outcome is not ("success" or "http400" or "unknown")
                || x.Contract is not ("0000986701" or "0000986702" or "0000986703")
                || (x.Address.Length>0 && (!Uri.TryCreate(x.Address,UriKind.Absolute,out var u) || u.Host!="127.0.0.1"))))
            throw new InvalidOperationException("LOCAL_DEMO_EXPORT_STALE_OR_FOREIGN");
        return doc;
    }
    public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filter,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows=Read().Scenarios.Where(x=>x.IntegrationId!=Guid.Empty).Select(x=>new MonitorIntegracao
        {
            IntegracaoId=x.IntegrationId,Documento=x.Contract,CorrelationId=x.CorrelationId,
            Operacao="SAP_CONTRATO_TOLEDO",EstadoMotor=x.MotorState,CriadoEm=x.CreatedUtc,AtualizadoEm=x.UpdatedUtc,
            Fluxo="SIMULACAO LOCAL / "+x.Outcome,EtapaFuncional=x.JournalStage,StatusFuncional=x.JournalStatus,
            UltimoResultado=x.Event,UltimoErro=x.JournalError,ProtocoloGuardian=x.ProtocolId?.ToString()
        }).Where(x=>(string.IsNullOrWhiteSpace(filter.Documento)||x.Documento!.Contains(filter.Documento))
            && (string.IsNullOrWhiteSpace(filter.CorrelationId)||x.CorrelationId.Contains(filter.CorrelationId))
            && (string.IsNullOrWhiteSpace(filter.Estado)||x.EstadoMotor==filter.Estado)
            && (string.IsNullOrWhiteSpace(filter.Operacao)||x.Operacao.Contains(filter.Operacao))
            && (filter.De is null||x.CriadoEm>=filter.De) && (filter.Ate is null||x.CriadoEm<=filter.Ate)
            && (filter.Grupo=="TODOS" || filter.Grupo=="INDETERMINADOS"&&x.EstadoMotor=="RESULTADO_INDETERMINADO"
                || filter.Grupo=="FALHAS"&&x.EstadoMotor=="FALHA_DEFINITIVA"
                || filter.Grupo=="PENDENTES"&&x.EstadoMotor is "PENDENTE" or "EM_PROCESSAMENTO"))
            .OrderBy(x=>x.CriadoEm).ToArray();
        return Task.FromResult(new MonitorSnapshot(true,false,rows));
    }
    public Task<MonitorDetalhe> DetalharAsync(Guid id,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row=Read().Scenarios.Single(x=>x.IntegrationId==id);
        return Task.FromResult(new MonitorDetalhe([],row.History.Select(x=>new MonitorAuditoria(x.Event,x.Utc,null,null)).ToArray(),[]));
    }
    public Task<GuardianProtocolSnapshot> PreviewAsync(Guid id,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var doc=Read();var row=doc.Scenarios.Single(x=>x.IntegrationId==id);
        return Task.FromResult(new GuardianProtocolSnapshot
        {
            IntegrationId=id,Contract=row.Contract,Version=1,SnapshotFingerprint=row.Fingerprint,
            CanSend=false,BlockCode=LockReason,State=row.Event,HttpStatus=row.HttpStatus,ProtocolId=row.ProtocolId,
            Endpoint=row.Address,Method="OBSERVACAO LOCAL",AuthenticationContext="SIMULACAO LOCAL",
            PayloadJson=row.PayloadJson,SafeBusinessMessage=row.SafeBusinessMessage,
            LocalSimulationDetails=$"SIMULACAO LOCAL — NAO E GUARDIAN HML REAL\r\nCenario: {row.Outcome} | Contrato SAP sintetico: {row.Contract}\r\nIntegrationId: {id}\r\nCorrelacao: {row.CorrelationId}\r\nRecepcao: {row.Reception} | H4: {row.H4}\r\nWorker automatico: {row.Worker}\r\nSOAP simulado: {row.Soap} | REST simulado: {row.Rest}\r\nProtocolo simulado: {row.ProtocolId?.ToString()??"-"}\r\nJournal: {row.JournalStage} / {row.JournalStatus}\r\nMotor: {row.MotorState} | Erro: {row.JournalError??"-"}\r\nHTTP REST: {row.HttpStatus?.ToString()??"-"} | Mensagem: {row.SafeBusinessMessage??"-"}\r\nObservado: {row.UpdatedUtc:O}\r\nLeitura backend: {doc.CapturedUtc:O}\r\nH4/core/journal em memoria. Nenhuma durabilidade PostgreSQL comprovada.\r\n\r\nEVENTOS OBSERVADOS\r\n"+
                string.Join("\r\n",row.History.Select(x=>$"{x.Utc:HH:mm:ss.fff} {x.Event}"))
        });
    }
    public Task<GuardianProtocolSnapshot> SendAsync(Guid id,string hash,string previewId,bool confirm,CancellationToken ct)
        =>throw new GuardianProtocolException("LOCAL_SIMULATION_SEND_FORBIDDEN");
}

public sealed record LocalSimulationDocument
{
    public string Schema {get;init;}="";public Guid RunId{get;init;}public bool Simulated{get;init;}
    public DateTimeOffset CapturedUtc{get;init;}public LocalSimulationFrame[] Scenarios{get;init;}=[];
}
public sealed record LocalSimulationFrame
{
    public string Outcome{get;init;}="";public string Contract{get;init;}="";public Guid IntegrationId{get;init;}
    public string CorrelationId{get;init;}="";public string Fingerprint{get;init;}="";public string Address{get;init;}="";
    public string Event{get;init;}="";public string MotorState{get;init;}="PENDENTE";
    public string Reception{get;init;}="NAO OBSERVADA";public string H4{get;init;}="NAO OBSERVADO";
    public string Worker{get;init;}="NAO OBSERVADO";public string Soap{get;init;}="NAO OBSERVADO";public string Rest{get;init;}="NAO OBSERVADO";
    public string JournalStage{get;init;}="";public string JournalStatus{get;init;}="";public string? JournalError{get;init;}
    public int? HttpStatus{get;init;}public long? ProtocolId{get;init;}public string? SafeBusinessMessage{get;init;}
    public string? PayloadJson{get;init;}public DateTimeOffset CreatedUtc{get;init;}public DateTimeOffset UpdatedUtc{get;init;}
    public LocalSimulationEvent[] History{get;init;}=[];
}
public sealed record LocalSimulationEvent(string Event,DateTimeOffset Utc);
