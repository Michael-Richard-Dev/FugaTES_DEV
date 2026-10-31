using System.Net.Http.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms.Monitor;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Persistencia.Consultas.UnifiedDev;

namespace R165AL.Desktop;

// Read connector only. Guardian credentials, adapters and write routes stay off Windows.
public sealed class PhysicalMonitorClient : IMonitorReadRepository, IContratosAcmReadRepository, IGuardianProtocolClient, FugaTES.Aplicacao.Consultas.RecebimentosToledo.IRecebimentosToledoReadRepository, FugaTES.Aplicacao.Consultas.RecebimentosToledo.IToledoSapJsonReadRepository, IDisposable
{
    private readonly HttpClient http;
    private readonly PilotPlan plan;
    private readonly Guid id;
    private readonly bool unified;
    public bool IsPilotOnly=>!unified;
    private readonly Func<CancellationToken,Task>? prepareRead;
    public PhysicalReadConnection Connection{get;private set;}=new();
    public event Action? ConnectionChanged;
    public bool Configured => false;
    public bool PreviewAvailable => true;
    public string PreviewSource => unified?"POSTGRESQL DEV REAL / API PRIVADA / NATUREZA POR REGISTRO":"R165AL POSTGRESQL REAL / GUARDIAN SIMULADO / API SOMENTE LEITURA";
    public string LockReason => "ENVIO MANUAL PROIBIDO - TESTE FISICO UNITARIO";

    public PhysicalMonitorClient(Uri endpoint, string readKey, PilotPlan plan, Guid id, HttpMessageHandler handler,
        Func<CancellationToken,Task>? prepareRead=null,bool unified=false)
    {
        plan.Validate();
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.Port != 18761 ||
            endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || string.IsNullOrWhiteSpace(readKey) || readKey.Any(char.IsControl) || id == Guid.Empty)
            throw new InvalidOperationException("R165AL_PRIVATE_READ_BINDING_REQUIRED");
        this.plan = plan; this.id = id;this.prepareRead=prepareRead;this.unified=unified;
        http = new HttpClient(handler, true) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = unified?4194304:262144 };
        http.DefaultRequestHeaders.Add("X-R165AL-Read-Key", readKey);
    }

    private async Task<PhysicalFrame> ReadAsync(CancellationToken ct)
    {
        try
        {
        if(prepareRead is not null)await prepareRead(ct);
        using var response = await http.GetAsync($"/r165al/readback/{id:D}", ct);
        if(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new PhysicalConnectionException("READ_KEY_REJECTED");
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)throw new PhysicalConnectionException("INTEGRATION_NOT_FOUND");
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<PhysicalFrame>(ct)
            ?? throw new InvalidOperationException("R165AL_READBACK_EMPTY");
        data.Validate(plan, id);
        Connection=new(){Connected=true,Cluster=data.Cluster,ServerPort=data.Port,Database=data.Database,Role=data.Role,
            LastSuccessUtc=data.CapturedUtc,Message="Leitura física validada pela API privada."};
        ConnectionChanged?.Invoke();
        return data;
        }
        catch(Exception error) when(!ct.IsCancellationRequested)
        {
            var code=error is PhysicalConnectionException specific?specific.Code:
                error is OperationCanceledException?"READBACK_TIMEOUT":
                error is InvalidOperationException?error.Message:"BACKEND_UNAVAILABLE";
            Connection=Connection.Failure(code);ConnectionChanged?.Invoke();throw;
        }
    }

    public async Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filter, CancellationToken ct)
    {
        if(unified)
        {
            var snapshot=await ReadUnifiedAsync<MonitorSnapshot>("monitor",null,ct);
            bool M(string? f,string? value)=>string.IsNullOrWhiteSpace(f)||value?.Contains(f.Trim(),StringComparison.OrdinalIgnoreCase)==true;
            return snapshot with {Integracoes=snapshot.Integracoes.Where(r=>M(filter.Documento,r.Documento)&&M(filter.CorrelationId,r.CorrelationId)&&M(filter.Estado,r.EstadoMotor)&&
                (M(filter.Operacao,r.Operacao)||M(filter.Operacao,r.Fluxo))&&(filter.De is null||r.CriadoEm>=filter.De)&&(filter.Ate is null||r.CriadoEm<=filter.Ate)&&
                (filter.Grupo=="TODOS"||filter.Grupo=="FALHAS"&&r.EstadoMotor is "FALHA_DEFINITIVA" or "AGUARDANDO_REPROCESSAMENTO"||
                filter.Grupo=="INDETERMINADOS"&&r.EstadoMotor=="RESULTADO_INDETERMINADO"||filter.Grupo=="PENDENTES"&&(r.StatusFuncional=="AGUARDANDO_CALLBACK"||r.EstadoMotor is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_ACAO_MANUAL" or "AGUARDANDO_REPROCESSAMENTO" or "RESULTADO_INDETERMINADO"))).ToArray()};
        }
        var f = await ReadAsync(ct);
        bool Match(string? value, string? actual) => string.IsNullOrWhiteSpace(value) || actual?.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        var included = Match(filter.Documento, f.Contract) && Match(filter.CorrelationId, f.CorrelationId) &&
            Match(filter.Estado, f.State) && Match(filter.Operacao, "SAP_CONTRATO_TOLEDO") &&
            (filter.De is null || f.CreatedUtc >= filter.De) && (filter.Ate is null || f.CreatedUtc <= filter.Ate) &&
            (filter.Grupo == "TODOS" || filter.Grupo == "FALHAS" && f.State == "FALHA_DEFINITIVA" ||
             filter.Grupo == "INDETERMINADOS" && f.State == "RESULTADO_INDETERMINADO" ||
             filter.Grupo == "PENDENTES" && f.State is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_REPROCESSAMENTO" or "AGUARDANDO_ACAO_MANUAL");
        return new(true, false, included ? [new MonitorIntegracao {
            IntegracaoId = id, Documento = f.Contract, Operacao = "SAP_CONTRATO_TOLEDO", CorrelationId = f.CorrelationId,
            EstadoMotor = f.State, CriadoEm = f.CreatedUtc, Tentativa = f.AttemptId,
            Fluxo = "R165AL GUARDIAN SIMULADO", StatusFuncional = f.Status, EtapaFuncional = f.Stage,
            ProtocoloGuardian = f.Protocol, UltimoErro = f.Error, UltimoResultado = "POSTGRESQL REAL; GUARDIAN SIMULADO"
        }] : []);
    }

    public async Task<MonitorAcmDetalhe> DetalharAcmAsync(Guid target, CancellationToken ct)
    {
        if(unified)return await ReadUnifiedAsync<MonitorAcmDetalhe>("detail",target.ToString("D"),ct);
        if (target != id) throw new InvalidOperationException("R165AL_FOREIGN_INTEGRATION");
        var f = await ReadAsync(ct);
        return new(new([], [], []), new MonitorAcmDados {
            ContratosDisponiveis = true, Contrato = f.Contract, Centro = "3011", Versao = f.H4Version,
            CodigoGuardian = f.ConfirmedCode, Referencia = f.Reference, Protocolo = f.H4Protocol, RecebidoEm = f.CreatedUtc
        });
    }
    public async Task<MonitorDetalhe> DetalharAsync(Guid target, CancellationToken ct) => (await DetalharAcmAsync(target, ct)).Detalhe;

    public async Task<GuardianProtocolSnapshot> PreviewAsync(Guid target, CancellationToken ct)
    {
        if(unified)
        {
            var v=await ReadUnifiedAsync<DevProtocolView>("protocol",target.ToString("D"),ct);
            if(v.Integration.IntegracaoId!=target)throw new InvalidOperationException("UNIFIED_DEV_FOREIGN_INTEGRATION");
            var storedProtocol=v.Acm.Protocolo??v.Integration.ProtocoloGuardian;
            return new(){IntegrationId=target,Contract=v.Acm.Contrato??v.Integration.Documento??"",Version=checked((int)(v.Acm.Versao??0)),
                CanSend=false,BlockCode=LockReason,State=v.Integration.EstadoMotor,ProtocolId=long.TryParse(storedProtocol,out var number)?number:null,
                AuthenticationContext=v.Origin,Method="GET READ ONLY",Endpoint=http.BaseAddress+"dev/read/protocol",
                SafeBusinessMessage="Estado persistido; nenhuma chamada empresarial executada nesta consulta.",
                LocalSimulationDetails=$"POSTGRESQL DEV REAL | fugates_dev | porta física 5433\r\nNatureza: {v.Origin}\r\nContrato: {v.Acm.Contrato??v.Integration.Documento}\r\nIntegrationId: {target}\r\nH4v{v.Acm.Versao?.ToString()??"-"} | Estado: {v.Integration.EstadoMotor}\r\n{(v.KnownSynthetic?"Protocolo SIMULADO":"Protocolo Guardian conforme persistência")}: {storedProtocol??"-"}\r\nOrigem da entrega SAP não é inferida da presença do registro. Sem POST ou replay."};
        }
        if (target != id) throw new GuardianProtocolException("R165AL_FOREIGN_INTEGRATION");
        var f = await ReadAsync(ct);
        return new() {
            IntegrationId = id, Contract = f.Contract, Version = 1, SnapshotFingerprint = f.Fingerprint,
            PayloadJson = f.PayloadJson, CanSend = false, BlockCode = LockReason, State = f.State,
            Method = "GET READBACK", Endpoint = http.BaseAddress + $"r165al/readback/{id:D}",
            AuthenticationContext = "BACKEND PRIVADO - GUARDIAN SIMULADO", ProtocolId = long.TryParse(f.Protocol, out var protocol) ? protocol : null,
            SafeBusinessMessage = "Resultado persistido do teste sintetico. Nao houve chamada Guardian real.",
            LocalSimulationDetails = $"POSTGRESQL REAL / GUARDIAN SIMULADO\r\nContrato sintetico: {f.Contract}\r\nIntegrationId: {id}\r\nCorrelacao: {f.CorrelationId}\r\nH4v{f.H4Version} | Fingerprint: {f.Fingerprint}\r\nMotor: {f.State} | Journal: {f.Stage} / {f.Status}\r\nProtocolo SIMULADO persistido: {f.Protocol ?? "-"}\r\nTentativas fisicas: {f.Attempts} | Fencing: {f.Fencing}\r\nReinicio verificado: {f.RestartVerified}\r\nLeitura PostgreSQL READ ONLY: {f.CapturedUtc:O}\r\nHistorico detalhado nao consultado. Nenhuma entrega SAP ou Guardian real."
        };
    }

    public Task<GuardianProtocolSnapshot> SendAsync(Guid target, string hash, string previewId, bool confirm, CancellationToken ct)
        => Task.FromException<GuardianProtocolSnapshot>(new GuardianProtocolException("R165AL_MANUAL_SEND_FORBIDDEN"));
    public void Dispose() => http.Dispose();
    Task<IReadOnlyList<ContratoAcmLeitura>> IContratosAcmReadRepository.ConsultarAsync(string centro,CancellationToken ct)=>ReadContractsAsync(centro,ct);
    public Task<IReadOnlyList<ContratoAcmLeitura>> ReadContractsAsync(string centro,CancellationToken ct)
        =>ReadUnifiedAsync<IReadOnlyList<ContratoAcmLeitura>>("contracts",centro,ct);
    public Task<DevMasterdataSnapshot> ReadMasterdataAsync(string? bp,CancellationToken ct)
        =>ReadUnifiedAsync<DevMasterdataSnapshot>("masterdata",bp,ct);
    public Task<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? busca,CancellationToken ct)
        =>ReadUnifiedAsync<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentosToledoSnapshot>("toledo",FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentosToledoFiltro.Normalizar(busca),ct);
    public async Task<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid reciboId,CancellationToken ct)
    {
        if(reciboId==Guid.Empty)throw new ArgumentException("ReciboId invalido.");
        var data=await ReadUnifiedAsync<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentoToledoDocumento>("toledo-json",reciboId.ToString("D"),ct);
        if(data.ReciboId!=reciboId)throw new InvalidOperationException("TOLEDO_READ_FOREIGN_RECEIPT");
        return data;
    }
    public async Task<FugaTES.Aplicacao.Consultas.RecebimentosToledo.ToledoSapJsonFonte> ObterFonteJsonSapAsync(Guid reciboId,CancellationToken ct)
    {
        if(reciboId==Guid.Empty)throw new ArgumentException("ReciboId invalido.");
        var data=await ReadUnifiedAsync<FugaTES.Aplicacao.Consultas.RecebimentosToledo.ToledoSapJsonFonte>("toledo-sap-json",reciboId.ToString("D"),ct);
        if(data.Original.ReciboId!=reciboId)throw new InvalidOperationException("TOLEDO_READ_FOREIGN_RECEIPT");
        return data;
    }
    async Task<T> ReadUnifiedAsync<T>(string resource,string? selector,CancellationToken ct)
    {
        if(!unified)throw new PhysicalConnectionException("UNIFIED_DEV_ROUTE_UNAVAILABLE");
        try
        {
            if(prepareRead is not null)await prepareRead(ct);
            var path="/dev/read/"+resource+(selector is null?"":"?selector="+Uri.EscapeDataString(selector));
            using var response=await http.GetAsync(path,ct);
            if(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)throw new PhysicalConnectionException("READ_KEY_REJECTED");
            if(response.StatusCode==System.Net.HttpStatusCode.NotFound)throw new PhysicalConnectionException("UNIFIED_DEV_ROUTE_UNAVAILABLE");
            response.EnsureSuccessStatusCode();
            var envelope=await response.Content.ReadFromJsonAsync<DevReadEnvelope<T>>(ct)??throw new InvalidOperationException("UNIFIED_DEV_EMPTY_RESPONSE");
            envelope.Identity.Validate();if(envelope.Data is null)throw new InvalidOperationException("UNIFIED_DEV_EMPTY_RESPONSE");
            Connection=new(){Connected=true,Cluster=envelope.Identity.Cluster,ServerPort=envelope.Identity.Port,Database=envelope.Identity.Database,
                Role=envelope.Identity.Role,LastSuccessUtc=envelope.Identity.CapturedUtc,PilotOnly=false,Message="Leitura unificada DEV validada pela API privada."};
            ConnectionChanged?.Invoke();return envelope.Data;
        }
        catch(Exception error) when(!ct.IsCancellationRequested)
        {
            Connection=Connection.Failure(error is PhysicalConnectionException e?e.Code:error is OperationCanceledException?"READBACK_TIMEOUT":error.Message);
            ConnectionChanged?.Invoke();throw;
        }
    }
}
