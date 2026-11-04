using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.BusinessDocuments;
using Microsoft.Extensions.Options;

namespace FugaTES.HostIntegracao;

/// <summary>
/// Leva a pesagem ate o SAP sozinha: cria a remessa, ajusta cabecalho e itens, grava o texto da
/// NF-e. Os passos 3, 4 e 5, sem tela aberta e sem ninguem confirmando.
///
/// Desde 04/11/2026 e so ate ai: separacao e saida de mercadoria sao feitas dentro do SAP, em
/// todos os setores. O codigo delas continua aqui, atras de <see cref="EscopoExpedicaoSd"/> -
/// quando a chave volta, o motor retoma a separacao de casca e fibra sem nada ser reescrito.
///
/// O que ele NAO faz, deliberadamente:
///
///   - nao toca em pesagem sem correlacao. Sem a ordem, nao ha a que referenciar a remessa, e
///     adivinhar por parceiro e data acerta no teste e erra quando ha duas ordens no mesmo dia;
///   - nao repete o que ja tem desfecho, nem mesmo recusa. "VL/096 ordem incompleta" e "VL/150
///     sem saldo" tem causa fora de nos; insistir a cada volta marteria o SAP sem resolver, e a
///     retentativa depois de corrigida a causa continua sendo por clique, na tela;
///   - nao passa do teto por volta, para uma configuracao errada nao virar rajada;
///   - nao decide duplicidade por leitura previa: quem arbitra e o indice unico do PostgreSQL, o
///     mesmo que arbitra entre o motor e a tela do operador.
///
/// E usa o MESMO <see cref="ExecutorFluxoRemessaSd"/> da tela. O que acontece sozinho e o que o
/// operador confere a mao - nao uma copia que diverge com o tempo.
/// </summary>
public sealed class RemessaSdWorker(
    IOptions<SdProtocoloOptions> opcoes,
    IDevolutivasSdRepositorio devolutivas,
    IRemessasSdRepositorio remessas,
    ExecutorFluxoRemessaSd executor,
    TimeProvider relogio,
    ILogger<RemessaSdWorker> log,
    IExpedirRemessaSdSap? expedicao = null) : BackgroundService
{
    private readonly SdProtocoloOptions config = opcoes.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.RemessaAutomatica)
        {
            log.LogInformation("SD_REMESSA_MOTOR_DESLIGADO: SdProtocolo:RemessaAutomatica não está ligada.");
            return;
        }

        log.LogInformation(
            "SD_REMESSA_MOTOR_INICIADO: intervalo {Intervalo}, até {Teto} pesagem(ns) por volta. "
            + "Cria remessa, ajusta e grava o texto. Expedição (separação e saída): {Expedicao}.",
            config.Intervalo, config.RemessasPorVarredura,
            EscopoExpedicaoSd.ExpedicaoHabilitada
                ? (config.SaidaAutomatica
                    ? "separa em casca e fibra e posta a saída automaticamente"
                    : "separa em casca e fibra; a saída fica para o clique")
                : "feita dentro do SAP, fora do nosso fluxo");

        using var timer = new PeriodicTimer(config.Intervalo, relogio);
        do
        {
            try { await VarrerAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // Uma volta ruim nao derruba o motor: a proxima tenta de novo.
            catch (Exception erro)
            { log.LogError(erro, "SD_REMESSA_VOLTA_FALHOU: {Tipo}", erro.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));

        log.LogInformation("SD_REMESSA_MOTOR_PARADO.");
    }

    public async Task VarrerAsync(CancellationToken ct)
    {
        IReadOnlyList<DevolutivaSdLinha> pesagens;
        IReadOnlyList<RemessaSdLinha> conhecidas;
        try
        {
            pesagens = await devolutivas.ListarAsync(config.RemessasPorVarredura * 10, ct).ConfigureAwait(false);
            conhecidas = await remessas.ListarAsync(config.RemessasPorVarredura * 10, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            // Sem conseguir ler o registro nao se envia nada: conferir o que ja foi feito e
            // condicao para agir, e nao um detalhe que se possa pular quando o banco oscila.
            log.LogError(erro, "SD_REMESSA_REGISTRO_INDISPONIVEL: nada foi enviado nesta volta.");
            return;
        }

        var feitas = 0;
        var paradas = 0;
        foreach (var pesagem in pesagens)
        {
            if (feitas + paradas >= config.RemessasPorVarredura) break;
            ct.ThrowIfCancellationRequested();

            if (pesagem.Estado != EstadoDevolutivaSd.Correlacionada) continue;
            // Qualquer desfecho ja registrado tira a pesagem do automatico, inclusive recusa.
            if (conhecidas.Any(r => string.Equals(r.IdPesagem, pesagem.IdPesagem, StringComparison.Ordinal)))
                continue;

            if (await LevarAoSapAsync(pesagem, ct).ConfigureAwait(false)) feitas++; else paradas++;
        }

        // Registra TODA volta, inclusive a que nao fez nada - mesma razao do motor de protocolo:
        // volta sem acao fica indistinguivel de motor parado para quem le o journal.
        log.LogInformation(
            "SD_REMESSA_VOLTA: {Pesagens} pesagem(ns) lidas, {Feitas} concluída(s), "
            + "{Paradas} parada(s), {Fora} fora do automático.",
            pesagens.Count, feitas, paradas, pesagens.Count - feitas - paradas);
    }

    /// <summary>
    /// Criar, ajustar, gravar o texto. Para na primeira etapa que nao concluir: seguir adiante
    /// com a remessa pela metade so empilharia problema sobre problema.
    /// </summary>
    private async Task<bool> LevarAoSapAsync(DevolutivaSdLinha pesagem, CancellationToken ct)
    {
        var (setor, falhaSetor) = await executor.LerSetorAsync(pesagem.OrdemVenda!, ct).ConfigureAwait(false);
        if (setor is null)
        {
            log.LogWarning("SD_REMESSA_SEM_SETOR: {Pesagem} — {Falha}", pesagem.IdPesagem, falhaSetor);
            return false;
        }

        FluxoRemessaSd fluxo;
        RemessaSdRequest pedido;
        string json;
        try
        {
            fluxo = FluxoRemessaSd.Escolher(setor, pesagem.PesoLiquido);
            pedido = MapeadorRemessaSd.Montar(pesagem.OrdemVenda, pesagem.LinhaOrdem,
                pesagem.PesoLiquido, setor);
            json = MapeadorRemessaSd.Serializar(pedido);
        }
        catch (RemessaSdBloqueadaException bloqueio)
        {
            log.LogWarning("SD_REMESSA_BLOQUEADA: {Pesagem} — {Codigo}: {Detalhe}",
                pesagem.IdPesagem, bloqueio.Codigo, bloqueio.Detalhe);
            return false;
        }

        var criacao = await executor.CriarRemessaAsync(pesagem, setor, fluxo, pedido, json, ct)
            .ConfigureAwait(false);
        if (!criacao.Ok || criacao.NumeroRemessa is null)
        {
            log.LogWarning("SD_REMESSA_NAO_CRIADA: {Pesagem} — {Resumo}", pesagem.IdPesagem, criacao.Resumo);
            return false;
        }
        var numero = criacao.NumeroRemessa;
        log.LogInformation("SD_REMESSA_CRIADA: {Pesagem} — {Resumo}", pesagem.IdPesagem, criacao.Resumo);

        if (!await AjustarAsync(pesagem, numero, setor, ct).ConfigureAwait(false)) return false;
        if (!await TextoAsync(pesagem, numero, ct).ConfigureAwait(false)) return false;

        // Separacao: so em casca e fibra, e sempre DEPOIS do texto, que e a ordem do processo.
        if (!EscopoExpedicaoSd.PickingNoFugaTES(setor)) return true;
        if (expedicao is null)
        {
            log.LogWarning("SD_PICKING_INDISPONIVEL: {Pesagem} — setor {Setor} exige separação, "
                + "mas o cliente de expedição não está configurado.", pesagem.IdPesagem, setor);
            return false;
        }

        var picking = await executor.ConfirmarPickingAsync(pesagem.IdPesagem, numero, expedicao, ct)
            .ConfigureAwait(false);
        if (picking.Ok)
            log.LogInformation("SD_PICKING_OK: {Pesagem} — {Resumo}", pesagem.IdPesagem, picking.Resumo);
        else
        {
            log.LogWarning("SD_PICKING_NAO_CONFIRMADO: {Pesagem} — {Resumo}",
                pesagem.IdPesagem, picking.Resumo);
            return false;
        }

        // Saida de mercadoria: o ultimo passo, e o unico atras de chave propria. Sem ela o motor
        // para na separacao e deixa a saida para o clique - nao e falha, e o padrao.
        if (!config.SaidaAutomatica)
        {
            log.LogInformation("SD_SAIDA_MANUAL: {Pesagem} — separação feita; a saída de mercadoria "
                + "aguarda o clique (SdProtocolo:SaidaAutomatica desligada).", pesagem.IdPesagem);
            return true;
        }

        var saida = await executor.PostarSaidaAsync(pesagem.IdPesagem, numero, expedicao, ct)
            .ConfigureAwait(false);
        if (saida.Ok)
            log.LogInformation("SD_SAIDA_OK: {Pesagem} — {Resumo}", pesagem.IdPesagem, saida.Resumo);
        else
            log.LogWarning("SD_SAIDA_NAO_POSTADA: {Pesagem} — {Resumo}", pesagem.IdPesagem, saida.Resumo);
        return saida.Ok;
    }

    private async Task<bool> AjustarAsync(DevolutivaSdLinha pesagem, string numero, string setor,
        CancellationToken ct)
    {
        AjusteCabecalhoRemessaSdRequest cabecalho;
        bool zerarItens;
        try
        {
            zerarItens = AjusteRemessaSd.AjustaPeso(setor);
            cabecalho = MapeadorAjusteRemessaSd.MontarCabecalho(
                setor, pesagem.PesoBruto, pesagem.PesoLiquido, pesagem.Placa);
        }
        catch (RemessaSdBloqueadaException bloqueio)
        {
            log.LogWarning("SD_AJUSTE_BLOQUEADO: {Pesagem} — {Codigo}: {Detalhe}",
                pesagem.IdPesagem, bloqueio.Codigo, bloqueio.Detalhe);
            return false;
        }

        var lida = await executor.LerRemessaAsync(numero, ct).ConfigureAwait(false);
        if (lida is null)
        {
            log.LogWarning("SD_AJUSTE_SEM_LEITURA: {Pesagem} — remessa {Numero} não pôde ser lida.",
                pesagem.IdPesagem, numero);
            return false;
        }

        var passo = await executor.AjustarRemessaAsync(pesagem.IdPesagem, numero, lida, cabecalho,
            zerarItens, MapeadorAjusteRemessaSd.Serializar(cabecalho), ct).ConfigureAwait(false);
        if (passo.Ok) log.LogInformation("SD_AJUSTE_OK: {Pesagem} — {Resumo}", pesagem.IdPesagem, passo.Resumo);
        else log.LogWarning("SD_AJUSTE_NAO_CONCLUIDO: {Pesagem} — {Resumo}", pesagem.IdPesagem, passo.Resumo);
        return passo.Ok;
    }

    private async Task<bool> TextoAsync(DevolutivaSdLinha pesagem, string numero, CancellationToken ct)
    {
        var complemento = MapeadorTextoNfeSd.LerComplemento(pesagem.PayloadJson);
        if (!complemento.TemAlgo)
        {
            // Sem nada a acrescentar nao ha texto a gravar, e isso nao e falha.
            log.LogInformation("SD_TEXTO_SEM_COMPLEMENTO: {Pesagem} — nada a acrescentar.", pesagem.IdPesagem);
            return true;
        }

        TextoNfeSdAtual atual;
        try { atual = await executor.LerTextoAsync(numero, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            // Sem o texto atual nao se compoe: o PATCH substitui o texto inteiro, e compor no
            // escuro apagaria o que veio da ordem de venda.
            log.LogWarning(erro, "SD_TEXTO_SEM_LEITURA: {Pesagem} — nada foi alterado.", pesagem.IdPesagem);
            return false;
        }

        string final;
        try { final = MapeadorTextoNfeSd.Compor(atual.Texto, complemento); }
        catch (RemessaSdBloqueadaException bloqueio)
        {
            log.LogWarning("SD_TEXTO_BLOQUEADO: {Pesagem} — {Codigo}", pesagem.IdPesagem, bloqueio.Codigo);
            return false;
        }

        if (string.Equals(final, atual.Texto, StringComparison.Ordinal))
        {
            log.LogInformation("SD_TEXTO_INALTERADO: {Pesagem} — o texto já contém tudo.", pesagem.IdPesagem);
            return true;
        }

        var passo = await executor.GravarTextoAsync(pesagem.IdPesagem, numero, atual, final, ct)
            .ConfigureAwait(false);
        if (passo.Ok) log.LogInformation("SD_TEXTO_OK: {Pesagem} — {Resumo}", pesagem.IdPesagem, passo.Resumo);
        else log.LogWarning("SD_TEXTO_NAO_GRAVADO: {Pesagem} — {Resumo}", pesagem.IdPesagem, passo.Resumo);
        return passo.Ok;
    }
}
