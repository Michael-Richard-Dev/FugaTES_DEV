using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Options;

namespace FugaTES.HostIntegracao;

/// <summary>
/// Motor do fluxo SD: varre as ordens de venda no SAP e cria no Guardian o protocolo das linhas
/// que ainda nao tem. Roda sem tela aberta e sem ninguem logado.
///
/// O que ele NAO faz, deliberadamente:
///   - nao alcanca ordens criadas antes do corte (o acervo antigo nao vai a balanca);
///   - nao cria mais que o teto por volta, para uma configuracao errada nao virar uma rajada;
///   - nao decide duplicidade por leitura previa: quem arbitra e o indice unico do PostgreSQL,
///     o mesmo que arbitra entre o motor e a tela do operador.
///
/// Falha de uma volta nao derruba o servico: o SAP cai, a rede oscila, e a proxima volta tenta de
/// novo. O que nao pode e uma falha passar calada - por isso toda volta registra o que fez.
/// </summary>
public sealed class SdProtocoloWorker(
    IOptions<SdProtocoloOptions> opcoes,
    IConsultaOrdensVendaSdSap consulta,
    IRegistroProtocolosSd registro,
    IEnviarProtocoloSdGateway guardian,
    TimeProvider relogio,
    ILogger<SdProtocoloWorker> log,
    MotorSdCredenciais? credenciais = null) : BackgroundService
{
    private readonly SdProtocoloOptions config = opcoes.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Enabled)
        {
            log.LogInformation("SD_MOTOR_DESLIGADO: a chave SdProtocolo:Enabled nao esta ligada.");
            return;
        }
        if (config.Reprovar() is string motivo)
        {
            // Subir com recorte invalido criaria protocolo real onde nao devia. Parar e mais seguro.
            log.LogError("SD_MOTOR_CONFIGURACAO_INVALIDA: {Motivo}", motivo);
            throw new InvalidOperationException(motivo);
        }

        // Confere as credenciais AQUI, e nao na primeira volta: assim um arquivo com permissao
        // errada aparece como falha na subida, com codigo proprio, em vez de uma excecao a cada
        // tres minutos no journal. Nenhum valor e registrado - so o fato de terem sido lidas.
        try { credenciais?.ConferirTodas(); }
        catch (CarrierServiceStartupException falha)
        {
            log.LogError("SD_MOTOR_CREDENCIAIS_INDISPONIVEIS: {Codigo}", falha.Message);
            throw;
        }

        log.LogInformation(
            "SD_MOTOR_INICIADO: intervalo {Intervalo}, corte {Corte}, ate {Teto} protocolo(s) por volta. "
            + "Credenciais conferidas.",
            config.Intervalo, config.CorteCriacao, config.ProtocolosPorVarredura);

        using var timer = new PeriodicTimer(config.Intervalo, relogio);
        do
        {
            try { await VarrerAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // Uma volta ruim nao pode derrubar o motor: a proxima tenta de novo.
            catch (Exception erro)
            {
                log.LogError(erro, "SD_MOTOR_VOLTA_FALHOU: {Tipo}", erro.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));

        log.LogInformation("SD_MOTOR_PARADO.");
    }

    /// <summary>Uma volta do motor. Publica para poder ser exercitada em teste sem o servico.</summary>
    public async Task VarrerAsync(CancellationToken ct)
    {
        var resultado = await consulta.ConsultarAsync(config.MontarFiltro(), ct).ConfigureAwait(false);
        if (resultado.Status != StatusConsultaSap.Sucesso || resultado.Dados is null)
        {
            log.LogWarning("SD_MOTOR_BUSCA_NAO_CONCLUIDA: {Status}. Nada foi criado.", resultado.Status);
            return;
        }

        // Le o registro UMA vez por volta. Ele nao decide a duplicidade - quem decide e o indice
        // unico, na hora de reservar - mas evita um POST por linha que ja se sabe criada.
        IReadOnlyList<ProtocoloSdRegistrado> conhecidos;
        try { conhecidos = await registro.LerAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            // Sem conseguir ler o registro nao se envia nada: conferir a duplicidade e condicao
            // para enviar, e nao um detalhe que se possa pular quando o banco esta fora.
            log.LogError(erro, "SD_MOTOR_REGISTRO_INDISPONIVEL: nada foi enviado nesta volta.");
            return;
        }

        var criador = new CriadorProtocoloSd(registro, guardian);
        var criados = 0;
        var recusados = 0;
        var pulados = 0;

        foreach (var linha in resultado.Dados)
        {
            if (criados + recusados >= config.ProtocolosPorVarredura) break;
            ct.ThrowIfCancellationRequested();

            // Cinto e suspensorio: o corte ja vai no $filter do SAP, mas quem cria protocolo real
            // confere de novo aqui. Ordem sem data de criacao nao passa - nao saber quando foi
            // criada nao e o mesmo que ser recente.
            if (linha.DataCriacao is not DateTime criada
                || DateOnly.FromDateTime(criada) < config.CorteCriacao) { pulados++; continue; }

            string chave;
            ProtocoloPreCadastroSdRequest pedido;
            try
            {
                chave = MapeadorProtocoloSd.ChaveNatural(linha);
                pedido = MapeadorProtocoloSd.Montar(linha, config.UrlParaRetorno!,
                    CadastroGuardianConhecido.Desconhecido);
            }
            // Linha que o mapeador bloqueia (campo faltando) e pulada: insistir a cada volta so
            // encheria o log. Ela continua visivel na tela, com o motivo.
            catch (ProtocoloSdBloqueadoException) { pulados++; continue; }

            // Desfecho ja registrado tira a linha do automatico, INCLUSIVE recusa: a causa de uma
            // recusa do Guardian (cadastro faltando) demora para ser resolvida, e insistir a cada
            // volta encheria o journal sem adiantar. O reenvio continua sendo por clique, na tela.
            if (conhecidos.Any(p => string.Equals(p.ChaveNatural, chave, StringComparison.Ordinal)))
                continue;

            // Ordem sem pedido do cliente nao e o esperado pelo negocio - decisao do operador em
            // 04/11/2026. Nao bloqueia: a referencia simplesmente para na linha, e a pesagem
            // acontece do mesmo jeito. Mas nao passa calada, senao o cadastro faltando so
            // apareceria na balanca, longe de quem pode corrigir.
            if (string.IsNullOrWhiteSpace(linha.PedidoDoCliente))
                log.LogWarning("SD_ORDEM_SEM_PEDIDO_CLIENTE: {Chave} — a referência do protocolo "
                    + "vai sem o pedido do cliente.", chave);

            var desfecho = await criador.ExecutarAsync(pedido, chave, linha.Centro!, ct).ConfigureAwait(false);
            if (desfecho.Criado)
            {
                criados++;
                log.LogInformation("SD_MOTOR_PROTOCOLO_CRIADO: {Chave}, id {Id}, HTTP {Status}, "
                    + "referência {Referencia}.",
                    chave, desfecho.ProtocoloId, desfecho.HttpStatus,
                    pedido.DadosProtocoloPreCadastro.NumeroReferencia);
            }
            else if (desfecho.NadaEnviado) { pulados++; }
            else
            {
                recusados++;
                // Sem o corpo da resposta: o resumo ja e a mensagem segura do Guardian.
                log.LogWarning("SD_MOTOR_PROTOCOLO_NAO_CRIADO: {Chave} — {Resumo}", chave, desfecho.Resumo);
            }
        }

        // Registra TODA volta, inclusive a que nao fez nada. Sem isto, uma varredura em que tudo
        // ja tinha protocolo fica indistinguivel de um motor parado - e quem olha o journal nao
        // tem como saber se o servico esta vivo.
        log.LogInformation(
            "SD_MOTOR_VOLTA: {Linhas} linha(s) lidas, {Criados} criado(s), {Recusados} recusado(s), "
            + "{Pulados} fora do recorte, {Conhecidas} ja com protocolo.",
            resultado.Dados.Count, criados, recusados, pulados,
            resultado.Dados.Count - criados - recusados - pulados);
    }
}
