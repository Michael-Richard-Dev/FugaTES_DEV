using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// Contrato de UI/orquestracao: NAO habilita conectores produtivos automaticamente.
public enum CategoriaMasterdataR3 { Transportadoras, Emissores, Motoristas, Veiculos, Materiais, Contratos }
public enum EstadoMasterdataR3 { Bloqueado, Pendente, EmProcessamento, Sincronizado, Parcial, Falha, Indeterminado, Cancelado }
public enum OrigemCicloMasterdataR3 { Manual, Automatica }
public enum SituacaoCicloMasterdataR3 { Executado, Bloqueado, Ocupado, Encerrado }

public sealed record ResultadoCategoriaMasterdataR3(
    EstadoMasterdataR3 Estado, int Processados, int Sincronizados, int Pendentes,
    int Falhas, string? ReferenciaCheckpoint, bool CheckpointDuravelmenteConfirmado);

public sealed record EstadoCategoriaMasterdataR3(
    CategoriaMasterdataR3 Categoria, EstadoMasterdataR3 Estado,
    DateTimeOffset? UltimaExecucaoUtc, int? Processados, int? Sincronizados,
    int? Pendentes, int? Falhas, string? Checkpoint, string Motivo);

public sealed record LogCicloMasterdataR3(DateTimeOffset QuandoUtc, CategoriaMasterdataR3? Categoria,
    OrigemCicloMasterdataR3 Origem, string Codigo);

public sealed record ResultadoExecucaoMasterdataR3(SituacaoCicloMasterdataR3 Situacao,
    int CategoriasExecutadas, int CategoriasBloqueadas);

public interface IAcaoCategoriaMasterdataR3
{
    CategoriaMasterdataR3 Categoria { get; }
    bool ContratoHomologado { get; }
    bool CheckpointDuravelDisponivel { get; }
    Task<ResultadoCategoriaMasterdataR3> ExecutarIncrementalAsync(CancellationToken cancellationToken);
}

// Ponte intencional: adaptar ao CarrierDesktopLifecycle existente APENAS apos auditar
// suas assinaturas. Nao criar API/endpoint/delta presumido nem habilitar worker.
public sealed class AcaoCategoriaDelegadaMasterdataR3 : IAcaoCategoriaMasterdataR3
{
    private readonly Func<CancellationToken, Task<ResultadoCategoriaMasterdataR3>> _executar;
    public CategoriaMasterdataR3 Categoria { get; }
    public bool ContratoHomologado { get; }
    public bool CheckpointDuravelDisponivel { get; }

    public AcaoCategoriaDelegadaMasterdataR3(CategoriaMasterdataR3 categoria,
        bool contratoHomologado, bool checkpointDuravelDisponivel,
        Func<CancellationToken, Task<ResultadoCategoriaMasterdataR3>> executar)
    {
        Categoria = categoria;
        ContratoHomologado = contratoHomologado;
        CheckpointDuravelDisponivel = checkpointDuravelDisponivel;
        _executar = executar ?? throw new ArgumentNullException(nameof(executar));
    }

    public Task<ResultadoCategoriaMasterdataR3> ExecutarIncrementalAsync(CancellationToken ct) => _executar(ct);
}

public sealed class OrquestradorCompartilhadoMasterdataR3
{
    public static readonly TimeSpan IntervaloAutomatico = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _exclusao = new(1, 1);
    private readonly CancellationTokenSource _vida = new();
    private readonly object _estadoLock = new();
    private readonly IReadOnlyDictionary<CategoriaMasterdataR3, IAcaoCategoriaMasterdataR3> _acoes;
    private readonly Dictionary<CategoriaMasterdataR3, EstadoCategoriaMasterdataR3> _estados;
    private readonly List<LogCicloMasterdataR3> _logs = new();
    private readonly TimeProvider _tempo;
    private readonly bool _execucaoDeNegocioExplicitamenteAutorizada;
    private readonly HashSet<CategoriaMasterdataR3> _indeterminadas = new();
    private bool _encerrado;
    private readonly Func<bool>? _runtimeAutorizado;
    private readonly Func<bool>? _singleCreateAutorizado;

    // FALSE por padrao: nenhum acionamento de um Carrier real antes de homologacao.
    // Testes controlados podem injetar TRUE com runners exclusivamente em memoria.
    public OrquestradorCompartilhadoMasterdataR3(
        IEnumerable<IAcaoCategoriaMasterdataR3>? acoes = null,
        bool execucaoDeNegocioExplicitamenteAutorizada = false,
        TimeProvider? tempo = null, Func<bool>? runtimeAutorizado = null, Func<bool>? singleCreateAutorizado = null)
    {
        _tempo = tempo ?? TimeProvider.System;
        _runtimeAutorizado = runtimeAutorizado;
        _singleCreateAutorizado = singleCreateAutorizado;
        _execucaoDeNegocioExplicitamenteAutorizada = execucaoDeNegocioExplicitamenteAutorizada;
        var lista = (acoes ?? Array.Empty<IAcaoCategoriaMasterdataR3>()).ToList();
        if (lista.Any(a => a is null) || lista.Select(a => a.Categoria).Distinct().Count() != lista.Count)
            throw new ArgumentException("DUPLICATE_OR_NULL_CATEGORY_RUNNER", nameof(acoes));
        _acoes = lista.ToDictionary(a => a.Categoria);
        _estados = Enum.GetValues<CategoriaMasterdataR3>().ToDictionary(c => c,
            c => new EstadoCategoriaMasterdataR3(c, EstadoMasterdataR3.Bloqueado,
                null, null, null, null, null, null, "NOT_HOMOLOGATED_OR_UNCONFIGURED"));
        ProximaVerificacaoUtc = _tempo.GetUtcNow() + IntervaloAutomatico;
    }

    public event Action? EstadoAtualizado;
    public DateTimeOffset ProximaVerificacaoUtc { get; private set; }

    public IReadOnlyList<EstadoCategoriaMasterdataR3> Estados()
    {
        lock (_estadoLock) return _estados.Values.OrderBy(e => e.Categoria).ToArray();
    }
    public IReadOnlyList<LogCicloMasterdataR3> Logs()
    {
        lock (_estadoLock) return _logs.ToArray();
    }

    public async Task<ResultadoExecucaoMasterdataR3> ExecutarAsync(
        OrigemCicloMasterdataR3 origem, CancellationToken ct = default)
    {
        if (_vida.IsCancellationRequested) return new(SituacaoCicloMasterdataR3.Encerrado, 0, 0);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _vida.Token);
        bool acquired;
        try { acquired = await _exclusao.WaitAsync(0, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(SituacaoCicloMasterdataR3.Encerrado, 0, 0); }
        if (!acquired)
        {
            Registrar(null, origem, "SHARED_LOCK_BUSY");
            return new(SituacaoCicloMasterdataR3.Ocupado, 0, 0);
        }

        var executadas = 0;
        var bloqueadas = 0;
        try
        {
            foreach (var categoria in Enum.GetValues<CategoriaMasterdataR3>())
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!_acoes.TryGetValue(categoria, out var acao) ||
                    !(_runtimeAutorizado?.Invoke() ?? _execucaoDeNegocioExplicitamenteAutorizada) ||
                    !acao.ContratoHomologado || !acao.CheckpointDuravelDisponivel ||
                    _indeterminadas.Contains(categoria))
                {
                    bloqueadas++;
                    DefinirEstado(categoria, new(categoria, EstadoMasterdataR3.Bloqueado,
                        null, null, null, null, null, null,
                        _indeterminadas.Contains(categoria) ? "INDETERMINATE_RECONCILIATION_REQUIRED" :
                        "CATEGORY_OR_WRITES_NOT_APPROVED"));
                    Registrar(categoria, origem, "CATEGORY_SKIPPED_FAIL_CLOSED");
                    continue;
                }

                DefinirEstado(categoria, new(categoria, EstadoMasterdataR3.EmProcessamento,
                    _tempo.GetUtcNow(), null, null, null, null, null, "IN_PROGRESS"));
                try
                {
                    var r = await acao.ExecutarIncrementalAsync(linked.Token).ConfigureAwait(false);
                    if (r is null || r.Processados < 0 || r.Sincronizados < 0 || r.Pendentes < 0 || r.Falhas < 0 ||
                        r.Sincronizados > r.Processados ||
                        (r.Estado == EstadoMasterdataR3.Sincronizado &&
                         (!r.CheckpointDuravelmenteConfirmado || string.IsNullOrWhiteSpace(r.ReferenciaCheckpoint))))
                    {
                        _indeterminadas.Add(categoria);
                        DefinirEstado(categoria, new(categoria, EstadoMasterdataR3.Indeterminado,
                            _tempo.GetUtcNow(), null, null, null, null, null, "DURABLE_RESULT_NOT_PROVEN"));
                        Registrar(categoria, origem, "DURABLE_RESULT_NOT_PROVEN");
                        break;
                    }
                    executadas++;
                    if (r.Estado == EstadoMasterdataR3.Indeterminado)
                        _indeterminadas.Add(categoria);
                    DefinirEstado(categoria, new(categoria, r.Estado, _tempo.GetUtcNow(),
                        r.Processados, r.Sincronizados, r.Pendentes, r.Falhas,
                        r.CheckpointDuravelmenteConfirmado ? r.ReferenciaCheckpoint : null,
                        r.Estado == EstadoMasterdataR3.Indeterminado ? "MANUAL_RECONCILIATION_REQUIRED" : "RESULT_REPORTED"));
                    Registrar(categoria, origem, r.Estado.ToString().ToUpperInvariant());
                    if (r.Estado == EstadoMasterdataR3.Indeterminado) break;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    DefinirEstado(categoria, new(categoria, EstadoMasterdataR3.Cancelado,
                        _tempo.GetUtcNow(), null, null, null, null, null, "APP_CLOSED_OR_CANCELLED"));
                    return new(SituacaoCicloMasterdataR3.Encerrado, executadas, bloqueadas);
                }
                catch
                {
                    _indeterminadas.Add(categoria); // sem retry automatico em resultado incerto
                    DefinirEstado(categoria, new(categoria, EstadoMasterdataR3.Indeterminado,
                        _tempo.GetUtcNow(), null, null, null, null, null, "EXECUTION_ERROR_RECONCILE_MANUALLY"));
                    Registrar(categoria, origem, "EXECUTION_FAILED_NO_AUTO_RETRY");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new(SituacaoCicloMasterdataR3.Encerrado, executadas, bloqueadas);
        }
        finally
        {
            if (origem == OrigemCicloMasterdataR3.Automatica)
                ProximaVerificacaoUtc = _tempo.GetUtcNow() + IntervaloAutomatico;
            _exclusao.Release();
            EstadoAtualizado?.Invoke();
        }
        return new(executadas == 0 ? SituacaoCicloMasterdataR3.Bloqueado :
            SituacaoCicloMasterdataR3.Executado, executadas, bloqueadas);
    }

    public void Encerrar()
    {
        lock (_estadoLock)
        {
            if (_encerrado) return;
            _encerrado = true;
        }
        _vida.Cancel();
        EstadoAtualizado?.Invoke();
    }

    // Individual BP request only. The approved Carrier queue is consumed by the next automatic cycle.
    // No batch/manual motor contract is assumed here.
    public async Task<CarrierManualResult> SolicitarAsync(Guid id,
        Func<CancellationToken,Task<CarrierManualResult>> solicitar,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_vida.Token);
        bool acquired;
        try {acquired=await _exclusao.WaitAsync(0,linked.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){return new(id,false,"CARRIER_REQUEST_NOT_READY");}
        if(!acquired)return new(id,false,"CARRIER_CYCLE_BUSY");
        try {
            if(!(_runtimeAutorizado?.Invoke()??_execucaoDeNegocioExplicitamenteAutorizada))return new(id,false,"CARRIER_REQUEST_NOT_READY");
            var result=await solicitar(linked.Token).ConfigureAwait(false);
            Registrar(CategoriaMasterdataR3.Transportadoras,OrigemCicloMasterdataR3.Manual,result.Code);
            return result;
        }finally{_exclusao.Release();EstadoAtualizado?.Invoke();}
    }


    public async Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual> ConsultarReadOnlyAsync(string bp,
        Func<CancellationToken,Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual>> consultar,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_vida.Token);
        bool acquired;
        try{acquired=await _exclusao.WaitAsync(0,linked.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"APP_CLOSED_OR_CANCELLED");}
        if(!acquired)return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"SHARED_LOCK_BUSY");
        try
        {
            var result=await consultar(linked.Token).ConfigureAwait(false);
            FugaTES.Aplicacao.Transportadoras.CarrierPersistencePolicy.ValidateCode(result.Motivo);
            Registrar(CategoriaMasterdataR3.Transportadoras,OrigemCicloMasterdataR3.Manual,result.Motivo);
            return result;
        }
        catch(OperationCanceledException) when(linked.IsCancellationRequested)
        {return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"APP_CLOSED_OR_CANCELLED");}
        catch{return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"MANUAL_READ_FAILED");}
        finally{_exclusao.Release();}
    }
    // Typed read-only bridge: exactly the same semaphore and lifetime as the R15 manual path.
    public async Task<ResultadoConsultaProtegidaMasterdataR3<T>> ExecutarConsultaProtegidaAsync<T>(
        Func<CancellationToken,Task<T>> consultar,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(consultar);
        if (_vida.IsCancellationRequested) return new(SituacaoConsultaProtegidaMasterdataR3.Encerrada, default);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_vida.Token);
        bool acquired;
        try { acquired=await _exclusao.WaitAsync(0,linked.Token).ConfigureAwait(false); }
        catch(OperationCanceledException)
        { return new(_vida.IsCancellationRequested?SituacaoConsultaProtegidaMasterdataR3.Encerrada:SituacaoConsultaProtegidaMasterdataR3.Cancelada,default); }
        if(!acquired)return new(SituacaoConsultaProtegidaMasterdataR3.Ocupada,default);
        try
        {
            var result=await consultar(linked.Token).ConfigureAwait(false);
            return new(_vida.IsCancellationRequested?SituacaoConsultaProtegidaMasterdataR3.Encerrada:
                linked.IsCancellationRequested?SituacaoConsultaProtegidaMasterdataR3.Cancelada:
                SituacaoConsultaProtegidaMasterdataR3.Executada,result);
        }
        catch(OperationCanceledException) when(linked.IsCancellationRequested)
        { return new(_vida.IsCancellationRequested?SituacaoConsultaProtegidaMasterdataR3.Encerrada:SituacaoConsultaProtegidaMasterdataR3.Cancelada,default); }
        finally { _exclusao.Release(); }
    }

    // R31 explicit manual write boundary; uses the same semaphore/lifetime, never the read-only bridge.
    public async Task<FugaTES.Aplicacao.Transportadoras.SingleCreateR31.SingleCreateResultR31> ExecutarCriacaoUnitariaAsync(
        Guid attemptId,Func<CancellationToken,Task<FugaTES.Aplicacao.Transportadoras.SingleCreateR31.SingleCreateResultR31>> execute,CancellationToken ct=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_vida.Token);
        FugaTES.Aplicacao.Transportadoras.SingleCreateR31.SingleCreateResultR31 Block(string code)=>new(attemptId,FugaTES.Aplicacao.Transportadoras.SingleCreateR31.SingleCreateStateR31.BLOCKED,code);
        bool acquired;
        try{acquired=await _exclusao.WaitAsync(0,linked.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){return Block("APP_CLOSED_OR_CANCELLED");}
        if(!acquired)return Block("SHARED_LOCK_BUSY");
        try
        {
            if(!(_singleCreateAutorizado?.Invoke()??_runtimeAutorizado?.Invoke()??_execucaoDeNegocioExplicitamenteAutorizada))return Block("SINGLE_CREATE_RUNTIME_NOT_APPROVED");
            // Preserve the use-case INDETERMINATE result after dispatch, even during shutdown.
            return await execute(linked.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){return Block("APP_CLOSED_OR_CANCELLED");}
        finally{_exclusao.Release();}
    }

    public async Task AguardarOciosidadeAsync()
    {
        await _exclusao.WaitAsync().ConfigureAwait(false);_exclusao.Release();
    }


    public async Task EncerrarAsync()
    {
        Encerrar();
        await _exclusao.WaitAsync().ConfigureAwait(false);
        _exclusao.Release();
    }

    private void DefinirEstado(CategoriaMasterdataR3 categoria, EstadoCategoriaMasterdataR3 novo)
    {
        lock (_estadoLock) _estados[categoria] = novo;
        EstadoAtualizado?.Invoke();
    }
    private void Registrar(CategoriaMasterdataR3? categoria, OrigemCicloMasterdataR3 origem, string codigo)
    {
        lock (_estadoLock)
        {
            if (_logs.Count == 200) _logs.RemoveAt(0);
            _logs.Add(new(_tempo.GetUtcNow(), categoria, origem, codigo));
        }
        EstadoAtualizado?.Invoke();
    }
}
