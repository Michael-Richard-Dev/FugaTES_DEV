using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
using Xunit;

namespace FugaTES.Testes;

public sealed class MasterdataWinFormsOrchestrationR3Tests
{
    private static ResultadoCategoriaMasterdataR3 Ok(string checkpoint = "CP-T1") =>
        new(EstadoMasterdataR3.Sincronizado, 2, 2, 0, 0, checkpoint, true);

    private sealed class Acao : IAcaoCategoriaMasterdataR3
    {
        private readonly Func<CancellationToken, Task<ResultadoCategoriaMasterdataR3>> _exec;
        public CategoriaMasterdataR3 Categoria { get; }
        public bool ContratoHomologado { get; }
        public bool CheckpointDuravelDisponivel { get; }
        public int Chamadas { get; private set; }
        public Acao(CategoriaMasterdataR3 categoria,
            Func<CancellationToken, Task<ResultadoCategoriaMasterdataR3>>? exec = null,
            bool homologado = true, bool checkpoint = true)
        {
            Categoria = categoria; ContratoHomologado = homologado;
            CheckpointDuravelDisponivel = checkpoint;
            _exec = exec ?? (_ => Task.FromResult(Ok()));
        }
        public async Task<ResultadoCategoriaMasterdataR3> ExecutarIncrementalAsync(CancellationToken ct)
        {
            Chamadas++;
            return await _exec(ct);
        }
    }

    [Fact]
    public async Task AUTO_INTERVAL_30SEC_AndAutomaticRoute()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), OrquestradorCompartilhadoMasterdataR3.IntervaloAutomatico);
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras);
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        var r = await m.ExecutarAsync(OrigemCicloMasterdataR3.Automatica);
        Assert.Equal(SituacaoCicloMasterdataR3.Executado, r.Situacao);
        Assert.Equal(1, acao.Chamadas);
    }

    [Fact]
    public async Task MANUAL_BUTTON_UsesSameExecutionPath()
    {
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras);
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        Assert.Equal(1, acao.Chamadas);
        Assert.Contains(m.Logs(), x => x.Origem == OrigemCicloMasterdataR3.Manual &&
            x.Codigo == "SINCRONIZADO");
    }

    [Fact]
    public async Task SHARED_LOCK_NO_OVERLAPPING_CYCLES()
    {
        var iniciou = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras, async ct =>
        {
            iniciou.TrySetResult(true);
            await liberar.Task.WaitAsync(ct);
            return Ok();
        });
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        var primeira = m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        await iniciou.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var segunda = await m.ExecutarAsync(OrigemCicloMasterdataR3.Automatica);
        Assert.Equal(SituacaoCicloMasterdataR3.Ocupado, segunda.Situacao);
        liberar.SetResult(true);
        await primeira;
        Assert.Equal(1, acao.Chamadas);
    }

    [Fact]
    public async Task APP_CLOSE_STOPS_WORK_AndRefusesAnotherCycle()
    {
        var iniciou = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras, async ct =>
        {
            iniciou.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok();
        });
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        var emExecucao = m.ExecutarAsync(OrigemCicloMasterdataR3.Automatica);
        await iniciou.Task.WaitAsync(TimeSpan.FromSeconds(5));
        m.Encerrar();
        Assert.Equal(SituacaoCicloMasterdataR3.Encerrado, (await emExecucao).Situacao);
        Assert.Equal(SituacaoCicloMasterdataR3.Encerrado,
            (await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual)).Situacao);
        Assert.Equal(1, acao.Chamadas);
    }

    [Fact]
    public async Task BLOCKED_CATEGORY_SKIPPED()
    {
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras, homologado: false);
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        Assert.Equal(SituacaoCicloMasterdataR3.Bloqueado,
            (await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual)).Situacao);
        Assert.Equal(0, acao.Chamadas);
        Assert.Null(m.Estados().Single(s => s.Categoria == CategoriaMasterdataR3.Transportadoras).Processados);
    }

    [Fact]
    public async Task CHECKPOINT_PER_CATEGORY_IndependentIdentities()
    {
        var t = new Acao(CategoriaMasterdataR3.Transportadoras, _ => Task.FromResult(Ok("CP-TRANSPORTADORA")));
        var e = new Acao(CategoriaMasterdataR3.Emissores, _ => Task.FromResult(Ok("CP-EMISSOR")));
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { t, e }, true);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        Assert.Equal("CP-TRANSPORTADORA", m.Estados().Single(s => s.Categoria == t.Categoria).Checkpoint);
        Assert.Equal("CP-EMISSOR", m.Estados().Single(s => s.Categoria == e.Categoria).Checkpoint);
    }

    [Fact]
    public async Task TIMEOUT_INDETERMINATE_NO_RETRY()
    {
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras, _ =>
            Task.FromResult(new ResultadoCategoriaMasterdataR3(EstadoMasterdataR3.Indeterminado, 1, 0, 1, 0, null, false)));
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Automatica);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        Assert.Equal(1, acao.Chamadas);
        Assert.Equal(EstadoMasterdataR3.Bloqueado,
            m.Estados().Single(s => s.Categoria == acao.Categoria).Estado);
        Assert.Contains("RECONCILIATION", m.Estados().Single(s => s.Categoria == acao.Categoria).Motivo);
    }

    [Fact]
    public async Task NO_UNAUTHORIZED_WRITE_DefaultFalseBlocksAll()
    {
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras);
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao });
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Automatica);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        Assert.Equal(0, acao.Chamadas);
    }

    [Fact]
    public async Task NO_UNPROVEN_CHECKPOINT_AS_SUCCESS()
    {
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras,
            _ => Task.FromResult(new ResultadoCategoriaMasterdataR3(EstadoMasterdataR3.Sincronizado, 1, 1, 0, 0, null, false)));
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        Assert.Equal(EstadoMasterdataR3.Indeterminado,
            m.Estados().Single(s => s.Categoria == acao.Categoria).Estado);
        Assert.Null(m.Estados().Single(s => s.Categoria == acao.Categoria).Checkpoint);
    }

    [Fact]
    public async Task FAILED_RUN_NO_AUTOMATIC_RETRY_AndNoFakeSuccess()
    {
        var acao = new Acao(CategoriaMasterdataR3.Transportadoras,
            _ => throw new TimeoutException("no sensitive data"));
        var m = new OrquestradorCompartilhadoMasterdataR3(new[] { acao }, true);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Manual);
        await m.ExecutarAsync(OrigemCicloMasterdataR3.Automatica);
        Assert.Equal(1, acao.Chamadas);
        Assert.DoesNotContain(m.Estados(), s => s.Categoria == acao.Categoria && s.Estado == EstadoMasterdataR3.Sincronizado);
    }

    [Fact]
    public void CATEGORIES_PRESENT_WITHOUT_HOMOL_OVERRIDE()
    {
        var m = new OrquestradorCompartilhadoMasterdataR3();
        Assert.Equal(6, m.Estados().Count);
        Assert.All(m.Estados(), estado => Assert.Equal(EstadoMasterdataR3.Bloqueado, estado.Estado));
    }
}
