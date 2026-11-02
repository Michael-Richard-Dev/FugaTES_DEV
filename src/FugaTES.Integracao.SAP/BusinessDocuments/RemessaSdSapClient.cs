using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>Desfecho de UMA tentativa de criar remessa. Guarda o que serve para auditar.</summary>
public sealed record CriacaoRemessaSd(
    bool Criada, int HttpStatus, string? NumeroRemessa,
    IReadOnlyList<MensagemSap> MensagensSap, string PayloadSha256)
{
    /// <summary>Texto para a tela, com a causa antes da consequencia.</summary>
    public string Resumo => LeitorErroSapOData.Resumir(MensagensSap);
}

public interface ICriarRemessaSdSap
{
    Task<CriacaoRemessaSd> CriarAsync(RemessaSdRequest pedido, CancellationToken ct);
}

/// <summary>
/// POST em API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader.
///
/// Escrita no SAP exige CSRF: o executor busca o token e reenvia os cookies da mesma sessao.
/// Toda a lista errordetails e preservada - foi uma mensagem "info" ali dentro que explicou a
/// recusa de 01/11/2026, enquanto o codigo do topo so dizia a consequencia.
/// </summary>
public sealed class RemessaSdSapClient(SapODataHttpExecutor executor) : ICriarRemessaSdSap
{
    public const string Rota = "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader";

    public async Task<CriacaoRemessaSd> CriarAsync(RemessaSdRequest pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var uri = executor.ConstruirUri(Rota);

        // Serializa UMA vez: a impressao digital e os bytes enviados descrevem o mesmo pedido.
        var bytes = Encoding.UTF8.GetBytes(MapeadorRemessaSd.Serializar(pedido));
        var digital = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        var token = await executor.ObterTokenCsrfAsync(uri, ct).ConfigureAwait(false);

        // A ordem de venda tambem pode estar travada por uma gravacao anterior - nossa ou de
        // outro usuario. A trava e transitoria e a recusa garante que nada foi criado, entao
        // insistir aqui nao arrisca uma segunda remessa. Ver TravaSapOData.
        var resposta = await TravaSapOData.InsistirSeTravadoAsync(async cancelamento =>
        {
            using var conteudo = new ByteArrayContent(bytes);
            conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var r = await executor
                .EnviarComCsrfAsync(HttpMethod.Post, uri, conteudo, token, ifMatch: null, cancelamento)
                .ConfigureAwait(false);
            var ok = r.Status is HttpStatusCode.Created or HttpStatusCode.OK;
            return (Resposta: r, Ok: ok, Mensagens: ok ? [] : LeitorErroSapOData.Ler(r.Body));
        }, x => x.Ok, x => x.Mensagens, ct).ConfigureAwait(false);

        var status = (int)resposta.Resposta.Status;
        var criada = resposta.Ok;
        var numero = criada ? MapeadorRemessaSd.LerNumeroRemessa(resposta.Resposta.Body) : null;
        var mensagens = resposta.Mensagens;

        // Criada sem numero e pior do que falha: a remessa existe no SAP e nos nao sabemos qual.
        if (criada && numero is null)
            return new(false, status, null,
                [new MensagemSap("SD_REMESSA_SEM_NUMERO", "error",
                    "O SAP aceitou a criação mas não devolveu DeliveryDocument. Confira no SAP antes de repetir.")],
                digital);

        return new(criada, status, numero, mensagens, digital);
    }
}
