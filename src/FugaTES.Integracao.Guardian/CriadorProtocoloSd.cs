using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Integracao.Guardian;

/// <summary>Desfecho de UMA tentativa de criar protocolo, do jeito que a tela e o motor precisam.</summary>
public sealed record DesfechoProtocoloSd(
    bool Criado, string Resumo, string? Detalhe, long? ProtocoloId, int HttpStatus)
{
    /// <summary>Nada saiu pela rede. Separa "nao enviei" de "enviei e deu errado".</summary>
    public bool NadaEnviado { get; init; }
}

/// <summary>
/// Reserva, envia e fecha o desfecho de um protocolo de pre-cadastro SD.
///
/// Existe para que o que acontece SOZINHO seja exatamente o que o operador confere a mao: a tela
/// e o motor da VM chamam este mesmo codigo, e nao duas copias que podem divergir com o tempo.
///
/// A ordem nao e negociavel. RESERVA primeiro: quem arbitra a corrida entre a tela e o motor e o
/// indice unico do banco, nao uma leitura previa que ficaria velha entre conferir e gravar. E se
/// o processo cair durante o POST, a linha fica ocupada como desfecho desconhecido, em vez de
/// livre para um segundo envio ao Guardian.
/// </summary>
public sealed class CriadorProtocoloSd(IRegistroProtocolosSd registro, IEnviarProtocoloSdGateway guardian)
{
    public async Task<DesfechoProtocoloSd> ExecutarAsync(
        ProtocoloPreCadastroSdRequest pedido, string chaveNatural, string centro, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        ArgumentException.ThrowIfNullOrWhiteSpace(chaveNatural);
        var corte = chaveNatural.IndexOf('/');
        if (corte <= 0)
            throw new ArgumentException("SD_CHAVE_NATURAL_INVALIDA", nameof(chaveNatural));

        var dados = pedido.DadosProtocoloPreCadastro;
        try
        {
            await registro.RegistrarAsync(new ProtocoloSdEnvio(
                ChaveNatural: chaveNatural,
                OrdemVenda: chaveNatural[..corte],
                LinhaOrdem: chaveNatural[(corte + 1)..],
                Centro: centro,
                CodigoItemGuardian: dados.CodigoItem,
                CodigoEmissorGuardian: dados.CodigoEmissor,
                ProtocoloId: null,
                Estado: EstadoProtocoloSd.DesfechoDesconhecido,
                HttpStatus: 0,
                PayloadSha256: GuardianProtocoloSdClient.Impressao(pedido),
                UrlRetorno: pedido.Configuracao.UrlParaRetorno,
                MensagemRecusa: null), ct).ConfigureAwait(false);
        }
        catch (ProtocoloSdJaRegistradoException)
        {
            return new(false, "Esta linha já tem protocolo — outro envio chegou primeiro. "
                + "Nada foi enviado.", null, null, 0) { NadaEnviado = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, $"Não foi possível reservar a linha ({ex.GetType().Name}). "
                + "Nada foi enviado.", null, null, 0) { NadaEnviado = true };
        }

        try
        {
            var resultado = await guardian.EnviarAsync(pedido, chaveNatural, ct).ConfigureAwait(false);
            await registro.AtualizarDesfechoAsync(chaveNatural, resultado.ProtocoloId,
                resultado.Sucesso ? EstadoProtocoloSd.Criado : EstadoProtocoloSd.Recusado,
                resultado.HttpStatus, resultado.MensagemSegura, ct).ConfigureAwait(false);

            return new(resultado.Sucesso,
                resultado.Sucesso
                    ? $"Protocolo criado. HTTP {resultado.HttpStatus}, "
                      + $"id {resultado.ProtocoloId?.ToString() ?? "não devolvido"}."
                    : $"Guardian recusou. HTTP {resultado.HttpStatus}.",
                (resultado.Sucesso ? "// ENVIADO — protocolo criado no Guardian" : "// RECUSADO pelo Guardian")
                    + "\r\n// HTTP " + resultado.HttpStatus
                    + "\r\n// id do protocolo: " + (resultado.ProtocoloId?.ToString() ?? "não devolvido")
                    + "\r\n// sha256 do payload: " + resultado.PayloadSha256
                    + (resultado.MensagemSegura is null ? "" : "\r\n// resposta: " + resultado.MensagemSegura),
                resultado.ProtocoloId, resultado.HttpStatus);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A reserva ja esta gravada como DESFECHO_DESCONHECIDO, que e a verdade: o POST pode
            // ter chegado. A linha segue bloqueada ate alguem conferir no Guardian.
            return new(false, $"Desfecho desconhecido ({ex.GetType().Name}: {ex.Message}). "
                + "A linha ficou bloqueada. Confira no Guardian antes de liberar.", null, null, 0);
        }
    }
}
