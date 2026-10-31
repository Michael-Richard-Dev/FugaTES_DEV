using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Aplicacao.CasosDeUso.Mvp;

public abstract class FluxoMvpCasoDeUso(
    FluxoMvp fluxo, IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados,
    IDecisoesFluxoMvp decisoes, TimeProvider timeProvider) : IProcessadorExternoIntegracao
{
    public FluxoMvp Fluxo { get; } = fluxo;
    public string Operacao => "MVP_" + Fluxo.ToString().ToUpperInvariant();
    public string Origem => Fluxo is FluxoMvp.MmIn101 or FluxoMvp.AcmInPesagem ? "GUARDIAN" : "SAP";
    public string Destino => Origem == "SAP" ? "GUARDIAN" : "SAP";

    public async Task<ResultadoFluxoMvp> RegistrarAsync(string identidade, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(identidade) || identidade != identidade.Trim())
            return new(StatusFluxoMvp.FalhaNaoReprocessavel, "", Operacao, Erro: "IDENTIDADE_INVALIDA");
        try
        {
            await resultados.ExigirDisponibilidadeAsync(cancellationToken);
            await motor.RegistrarCandidatoAsync(new(Origem, Destino, Operacao, identidade, Fingerprint(identidade), identidade),
                timeProvider.GetUtcNow(), cancellationToken);
            return new(StatusFluxoMvp.ProntoParaEnvio, identidade, Operacao);
        }
        catch (DecisaoFuncionalPendenteSapException exception)
        {
            return new(StatusFluxoMvp.AguardandoDecisaoFuncional, identidade, Operacao, DecisaoPendente: exception.Decisao);
        }
        catch (CapabilityMvpPendenteException exception)
        {
            return new(StatusFluxoMvp.AguardandoDependenciaExterna, identidade, Operacao, Erro: exception.Capability);
        }
    }

    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        var resultado = await ExecutarAsync(claim, cancellationToken);
        return resultado.Status switch
        {
            StatusFluxoMvp.Concluido or StatusFluxoMvp.AguardandoCallback => ResultadoProcessamentoExterno.Sucesso,
            StatusFluxoMvp.ResultadoIndeterminado => new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado),
            StatusFluxoMvp.FalhaReprocessavel => new(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura),
            StatusFluxoMvp.AguardandoDecisaoFuncional or StatusFluxoMvp.AguardandoDependenciaExterna => new(ClassificacaoFalhaIntegracao.FalhaInternaPreEnvio),
            _ => new(ClassificacaoFalhaIntegracao.FalhaDefinitiva)
        };
    }

    public virtual async Task<ResultadoFluxoMvp> ExecutarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ValidarClaimAsync(claim, cancellationToken))
            return new(StatusFluxoMvp.FalhaNaoReprocessavel, claim.CorrelationId, Operacao, Erro: "CLAIM_INVALIDO");
        var efeitos = new EfeitosFluxoMvp(new(StatusFluxoMvp.EmProcessamento, claim.CorrelationId, Operacao));
        ResultadoFluxoMvp resultado;
        try
        {
            await resultados.ExigirDisponibilidadeAsync(cancellationToken);
            var anterior = await resultados.ObterAsync(claim, cancellationToken);
            if (anterior?.Status is StatusFluxoMvp.Concluido or StatusFluxoMvp.AguardandoCallback or StatusFluxoMvp.ResultadoIndeterminado)
                return anterior;
            if (claim.FencingToken > 1 && anterior is null)
                resultado = new(StatusFluxoMvp.ResultadoIndeterminado, claim.CorrelationId, Operacao, Erro: "REPLAY_SEM_RESULTADO_DURAVEL");
            else
            {
                await ExigirDecisoesAsync(claim, cancellationToken);
                resultado = await ExecutarEtapaAsync(claim, efeitos, cancellationToken);
            }
        }
        catch (DecisaoFuncionalPendenteSapException exception)
        {
            resultado = efeitos.Contexto with { Status = efeitos.Iniciado ? StatusFluxoMvp.ResultadoIndeterminado : StatusFluxoMvp.AguardandoDecisaoFuncional,
                DecisaoPendente = exception.Decisao };
        }
        catch (CapabilityMvpPendenteException exception)
        {
            resultado = efeitos.Contexto with { Status = efeitos.Iniciado ? StatusFluxoMvp.ResultadoIndeterminado : StatusFluxoMvp.AguardandoDependenciaExterna,
                Erro = exception.Capability, ErroCategoria = "DEPENDENCIA" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !efeitos.Iniciado)
        {
            throw;
        }
        catch (Exception)
        {
            resultado = efeitos.Contexto with { Status = efeitos.Iniciado ? StatusFluxoMvp.ResultadoIndeterminado : StatusFluxoMvp.FalhaReprocessavel,
                Erro = efeitos.Iniciado ? "RESULTADO_EXTERNO_NAO_COMPROVADO" : "FALHA_PRE_ENVIO", ErroCategoria = "MVP" };
        }
        try
        {
            resultado = resultado.Normalizar();
            if (!await resultados.PersistirAsync(claim, resultado, cancellationToken))
                return new(efeitos.Iniciado ? StatusFluxoMvp.ResultadoIndeterminado : StatusFluxoMvp.AguardandoDependenciaExterna,
                    claim.CorrelationId, Operacao, Erro: "PROJECAO_REJEITADA");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested || efeitos.Iniciado)
        {
            return new(efeitos.Iniciado ? StatusFluxoMvp.ResultadoIndeterminado : StatusFluxoMvp.AguardandoDependenciaExterna,
                claim.CorrelationId, Operacao, Erro: "PROJECAO_NAO_COMPROVADA");
        }
        return resultado;
    }

    public string Fingerprint(string identidade) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Operacao + "\n" + identidade)));

    protected virtual Task<bool> ValidarClaimAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
        => Task.FromResult(claim.Operacao == Operacao && claim.Origem == Origem && claim.Destino == Destino &&
            claim.FingerprintSha256 == Fingerprint(claim.ChaveNegocio));

    protected virtual Task ExigirDecisoesAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        foreach (var decisao in DecisoesFluxoMvp.Para(Fluxo)) decisoes.Exigir(decisao);
        return Task.CompletedTask;
    }

    protected void ExigirDecisao(string decisao) => decisoes.Exigir(decisao);

    protected abstract Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken cancellationToken);

    protected static T ExigirConsulta<T>(ResultadoConsultaSap<T> resultado) where T : class
        => resultado.Status == StatusConsultaSap.Sucesso && resultado.Dados is not null
            ? resultado.Dados : throw new CapabilityMvpPendenteException("SAP_" + resultado.Status);

    protected static T ExigirEscrita<T>(ResultadoEscritaSap<T> resultado) where T : class
        => resultado.Status == StatusEscritaSap.Sucesso && resultado.Dados is not null
            ? resultado.Dados : throw new InvalidOperationException("SAP_WRITE_NOT_CONFIRMED");

    protected static void ValidarGuardian(PreCadastroRecebimentoGuardianSet26 comando)
    {
        if (comando.Id != 0 || comando.IdAgenda <= 0 ||
            new[] { comando.CodigoFluxo, comando.CodigoItem, comando.CodigoEmissor, comando.CodigoTransportadora,
                comando.CodigoContrato, comando.NumeroReferencia, comando.UrlParaRetorno }.Any(string.IsNullOrWhiteSpace) ||
            comando.CamposAdicionais is null || !new[] { 1000, 1002, 1003 }.All(id => comando.CamposAdicionais.Any(c => c.Numero == id && !string.IsNullOrWhiteSpace(c.Valor))))
            throw new CapabilityMvpPendenteException("GUARDIAN_DEPENDENCY_MISSING");
    }

    protected sealed class EfeitosFluxoMvp(ResultadoFluxoMvp contexto)
    {
        public ResultadoFluxoMvp Contexto { get; set; } = contexto;
        public bool Iniciado { get; private set; }
        public void MarcarEnvioIniciado() => Iniciado = true;
        public async Task<T> ExecutarAsync<T>(Func<Task<T>> envio) { Iniciado = true; return await envio(); }
        public async Task ExecutarAsync(Func<Task> envio) { Iniciado = true; await envio(); }
    }
}
