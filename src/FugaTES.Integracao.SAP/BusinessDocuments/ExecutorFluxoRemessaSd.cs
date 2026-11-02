using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.Sd;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>Desfecho de UMA etapa do fluxo, do jeito que a tela e o motor precisam ver.</summary>
public sealed record PassoFluxoSd(bool Ok, string Resumo, string? Detalhe = null)
{
    /// <summary>Nada saiu pela rede. Separa "nao enviei" de "enviei e deu errado".</summary>
    public bool NadaEnviado { get; init; }

    /// <summary>Numero da remessa, quando o passo foi a criacao dela.</summary>
    public string? NumeroRemessa { get; init; }
}

/// <summary>
/// Os passos 3, 4 e 5 do fluxo SD, em um lugar so: criar a remessa, ajustar cabecalho e itens,
/// gravar o texto da NF-e.
///
/// Existe para que o que acontece SOZINHO no motor da VM seja exatamente o que o operador executa
/// pela tela. A tela acrescenta previa e confirmacao antes de chamar; o motor chama direto. O que
/// vai ao SAP, e a ordem em que vai, e o mesmo codigo - nao duas copias que divergem com o tempo.
///
/// Em todos: RESERVA no banco, envia, fecha o desfecho. Nessa ordem. Uma resposta perdida deixa a
/// pesagem ocupada em vez de livre, porque supor que nada saiu e o que cria a segunda remessa, o
/// segundo peso ou o segundo texto.
/// </summary>
public sealed class ExecutorFluxoRemessaSd(
    IRemessasSdRepositorio remessas,
    IConsultarSetorAtividadeSd setores,
    ICriarRemessaSdSap criacao,
    IAjustarRemessaSdSap ajuste,
    IGravarTextoNfeSd textos)
{
    /// <summary>
    /// Setor de atividade da ordem, lido do SAP. Sem ele nao da para escolher entre litro e quilo,
    /// e supor um valor aqui criaria remessa com a quantidade errada.
    /// </summary>
    public async Task<(string? Setor, string? Falha)> LerSetorAsync(string ordemVenda, CancellationToken ct)
    {
        var consulta = await setores.ConsultarAsync(ordemVenda, ct).ConfigureAwait(false);
        if (consulta.Status != StatusConsultaSap.Sucesso || string.IsNullOrWhiteSpace(consulta.Dados))
            return (null, $"Não foi possível ler o setor de atividade da ordem {ordemVenda} "
                + $"({consulta.Status}). Sem ele não dá para escolher entre litro e quilo.");
        return (consulta.Dados.Trim(), null);
    }

    public async Task<PassoFluxoSd> CriarRemessaAsync(DevolutivaSdLinha linha, string setor,
        FluxoRemessaSd fluxo, RemessaSdRequest pedido, string json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(linha);
        var chave = linha.ChaveNatural ?? $"{linha.OrdemVenda}/{linha.LinhaOrdem}";
        var digital = Impressao(json);

        try
        {
            await remessas.ReservarAsync(new RemessaSdRegistro(
                IdPesagem: linha.IdPesagem, ChaveNatural: chave,
                OrdemVenda: linha.OrdemVenda!, LinhaOrdem: linha.LinhaOrdem!,
                SetorAtividade: setor, Quantidade: fluxo.Quantidade, Unidade: fluxo.Unidade,
                NumeroRemessa: null, Estado: EstadoRemessaSd.DesfechoDesconhecido,
                HttpStatus: 0, PayloadSha256: digital, MensagensSap: null)
                { PayloadJson = json }, ct).ConfigureAwait(false);
        }
        catch (RemessaSdJaCriadaException)
        {
            return new(false, $"Pesagem {linha.IdPesagem} já estava reservada; nada foi enviado ao SAP.")
                { NadaEnviado = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Reserva falhou por {erro.GetType().Name}: {erro.Message}. "
                + "Nada foi enviado ao SAP.") { NadaEnviado = true };
        }

        CriacaoRemessaSd desfecho;
        try { desfecho = await criacao.CriarAsync(pedido, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            // Fica DESFECHO_DESCONHECIDO: o POST pode ter chegado.
            return new(false, $"Envio interrompido por {erro.GetType().Name}: {erro.Message}. "
                + "Confira a remessa no SAP antes de repetir.");
        }

        var estado = desfecho.Criada ? EstadoRemessaSd.Criada : EstadoRemessaSd.Recusada;
        var mensagens = desfecho.MensagensSap.Count == 0 ? null : desfecho.Resumo;
        try
        {
            await remessas.FecharDesfechoAsync(linha.IdPesagem, desfecho.NumeroRemessa, estado,
                desfecho.HttpStatus, mensagens, ct).ConfigureAwait(false);
        }
        catch (Exception erro)
        {
            return new(false, $"HTTP {desfecho.HttpStatus}, remessa "
                + $"{desfecho.NumeroRemessa ?? "(sem número)"}, mas o desfecho não foi gravado "
                + $"({erro.GetType().Name}: {erro.Message}).");
        }

        return desfecho.Criada
            ? new(true, $"Remessa {desfecho.NumeroRemessa} criada para a ordem "
                + $"{linha.OrdemVenda}/{linha.LinhaOrdem} ({fluxo.Quantidade} {fluxo.Unidade}).")
                { NumeroRemessa = desfecho.NumeroRemessa }
            : new(false, $"SAP recusou (HTTP {desfecho.HttpStatus}): {desfecho.Resumo}");
    }

    public async Task<PassoFluxoSd> AjustarRemessaAsync(string idPesagem, string numero,
        RemessaSdLida lida, AjusteCabecalhoRemessaSdRequest cabecalho, bool zerarItens,
        string json, CancellationToken ct)
    {
        try { await remessas.ReservarAjusteAsync(idPesagem, Impressao(json), json, ct).ConfigureAwait(false); }
        catch (AjusteRemessaSdJaFeitoException)
        {
            return new(false, $"A remessa {numero} já tinha ajuste registrado; nada foi enviado.")
                { NadaEnviado = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Reserva do ajuste falhou por {erro.GetType().Name}: {erro.Message}. "
                + "Nada foi enviado ao SAP.") { NadaEnviado = true };
        }

        IReadOnlyList<EtapaAjusteRemessaSd> etapas;
        try { etapas = await ajuste.AjustarAsync(lida, cabecalho, zerarItens, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Ajuste interrompido por {erro.GetType().Name}: {erro.Message}. "
                + $"Confira a remessa {numero} no SAP antes de repetir.");
        }

        var tudoOk = etapas.Count > 0 && etapas.All(e => e.Ok);
        // Etapa por etapa: num ajuste parcial, saber ONDE parou e o que permite retomar no SAP.
        var detalhe = string.Join(" | ", etapas.Select(
            e => $"{e.Etapa}: HTTP {e.HttpStatus}{(e.Ok ? " ok" : " — " + e.Resumo)}"));

        try
        {
            await remessas.FecharAjusteAsync(idPesagem,
                tudoOk ? EstadoAjusteRemessaSd.Ajustada : EstadoAjusteRemessaSd.Recusada,
                etapas.LastOrDefault()?.HttpStatus ?? 0, detalhe, ct).ConfigureAwait(false);
        }
        catch (Exception erro)
        {
            return new(false, $"{detalhe}; mas o desfecho não foi gravado "
                + $"({erro.GetType().Name}: {erro.Message}).", detalhe);
        }

        return new(tudoOk,
            tudoOk ? $"Remessa {numero} ajustada. {detalhe}"
                   : $"Ajuste incompleto na remessa {numero}. {detalhe}", detalhe);
    }

    public async Task<PassoFluxoSd> GravarTextoAsync(string idPesagem, string numero,
        TextoNfeSdAtual atual, string final, CancellationToken ct)
    {
        try { await remessas.ReservarTextoAsync(idPesagem, Impressao(final), final, ct).ConfigureAwait(false); }
        catch (TextoNfeSdJaGravadoException)
        {
            return new(false, $"O texto da remessa {numero} já tinha gravação registrada; "
                + "nada foi enviado.") { NadaEnviado = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Reserva do texto falhou por {erro.GetType().Name}: {erro.Message}. "
                + "Nada foi enviado ao SAP.") { NadaEnviado = true };
        }

        GravacaoTextoNfeSd desfecho;
        try { desfecho = await textos.GravarAsync(numero, atual, final, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Gravação do texto interrompida por {erro.GetType().Name}: "
                + $"{erro.Message}. Confira a remessa {numero} no SAP.");
        }

        try
        {
            await remessas.FecharTextoAsync(idPesagem,
                desfecho.Ok ? EstadoTextoNfeSd.Gravado : EstadoTextoNfeSd.Recusado,
                desfecho.HttpStatus, desfecho.Criou,
                desfecho.MensagensSap.Count == 0 ? null : desfecho.Resumo, ct).ConfigureAwait(false);
        }
        catch (Exception erro)
        {
            return new(false, $"HTTP {desfecho.HttpStatus} no texto, mas o desfecho não foi gravado "
                + $"({erro.GetType().Name}: {erro.Message}).");
        }

        return new(desfecho.Ok,
            desfecho.Ok
                ? $"Texto da remessa {numero} {(desfecho.Criou ? "criado" : "atualizado")} "
                  + $"(HTTP {desfecho.HttpStatus})."
                : $"SAP recusou o texto (HTTP {desfecho.HttpStatus}): {desfecho.Resumo}",
            desfecho.Resumo);
    }

    /// <summary>
    /// Separacao de todos os itens. So existe em casca e fibra - ver <see cref="EscopoExpedicaoSd"/>.
    /// A saida de mercadoria NAO passa por aqui: ela e feita dentro do SAP.
    /// </summary>
    public async Task<PassoFluxoSd> ConfirmarPickingAsync(string idPesagem, string numero,
        IExpedirRemessaSdSap expedicao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expedicao);
        try
        {
            await remessas.ReservarExpedicaoAsync(idPesagem, EtapaExpedicaoSd.Picking, ct)
                .ConfigureAwait(false);
        }
        catch (ExpedicaoSdJaFeitaException)
        {
            return new(false, $"O picking da remessa {numero} já tem desfecho registrado; "
                + "nada foi enviado.") { NadaEnviado = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Reserva do picking falhou por {erro.GetType().Name}: "
                + $"{erro.Message}. Nada foi enviado ao SAP.") { NadaEnviado = true };
        }

        ExpedicaoSd desfecho;
        try { desfecho = await expedicao.ConfirmarPickingAsync(numero, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro)
        {
            return new(false, $"Picking interrompido por {erro.GetType().Name}: {erro.Message}. "
                + $"Confira a remessa {numero} no SAP antes de repetir.");
        }

        try
        {
            await remessas.FecharExpedicaoAsync(idPesagem, EtapaExpedicaoSd.Picking,
                desfecho.Ok ? EstadoPickingSd.Confirmado : EstadoPickingSd.Recusado,
                desfecho.HttpStatus, desfecho.MensagensSap.Count == 0 ? null : desfecho.Resumo, ct)
                .ConfigureAwait(false);
        }
        catch (Exception erro)
        {
            return new(false, $"HTTP {desfecho.HttpStatus} no picking, mas o desfecho não foi "
                + $"gravado ({erro.GetType().Name}: {erro.Message}).");
        }

        return new(desfecho.Ok,
            desfecho.Ok
                ? $"Picking da remessa {numero} confirmado (HTTP {desfecho.HttpStatus})."
                : $"SAP recusou o picking (HTTP {desfecho.HttpStatus}): {desfecho.Resumo}",
            desfecho.Resumo);
    }

    /// <summary>Impressao digital do que foi enviado. Mesma conta nos tres passos.</summary>
    public static string Impressao(string json)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json)));

    /// <summary>Le a remessa para o ajuste. Dai vem o ETag e as chaves reais dos itens.</summary>
    public Task<RemessaSdLida?> LerRemessaAsync(string numero, CancellationToken ct)
        => ajuste.LerAsync(numero, ct);

    /// <summary>Le o texto atual. Sem ele nao se compoe nada: o PATCH substitui o texto inteiro.</summary>
    public Task<TextoNfeSdAtual> LerTextoAsync(string numero, CancellationToken ct)
        => textos.LerAsync(numero, ct);
}
