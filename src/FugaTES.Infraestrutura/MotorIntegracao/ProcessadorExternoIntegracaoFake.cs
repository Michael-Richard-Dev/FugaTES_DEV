using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Infraestrutura.MotorIntegracao;

public sealed class ProcessadorExternoIntegracaoFake : IProcessadorExternoIntegracao
{
    private readonly Queue<ResultadoProcessamentoExterno> resultados = [];
    private readonly List<ClaimIntegracaoMotor> chamadas = [];
    private readonly Func<bool>? transacaoAtiva;
    private TaskCompletionSource<ResultadoProcessamentoExterno>? processamentoPendente;

    public ProcessadorExternoIntegracaoFake(Func<bool>? transacaoAtiva = null)
    {
        this.transacaoAtiva = transacaoAtiva;
    }

    public int QuantidadeChamadas => chamadas.Count;

    public bool? TransacaoAtivaObservada { get; private set; }

    public bool CancelamentoObservado { get; private set; }

    public IReadOnlyCollection<ClaimIntegracaoMotor> Chamadas => chamadas;

    public void Configurar(params ResultadoProcessamentoExterno[] proximosResultados)
    {
        resultados.Clear();
        foreach (var resultado in proximosResultados)
        {
            resultados.Enqueue(resultado);
        }
    }

    public TaskCompletionSource<ResultadoProcessamentoExterno> ManterPendente()
    {
        processamentoPendente = new TaskCompletionSource<ResultadoProcessamentoExterno>(TaskCreationOptions.RunContinuationsAsynchronously);
        return processamentoPendente;
    }

    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        chamadas.Add(claim);
        TransacaoAtivaObservada = transacaoAtiva?.Invoke() ?? false;

        if (processamentoPendente is not null)
        {
            try
            {
                return await processamentoPendente.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancelamentoObservado = true;
                throw;
            }
        }

        var resultado = resultados.Count == 0 ? ResultadoProcessamentoExterno.Sucesso : resultados.Dequeue();
        return resultado;
    }
}
