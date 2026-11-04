using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Aplicacao.Servicos.Sd;

/// <summary>
/// Cadastro do Guardian conhecido por este processo: os codigos de PESSOA e de item que o
/// operador ja criou la. "Emissores" aqui e o nome do campo no Guardian, e desde 04/11/2026 o
/// valor conferido e o do recebedor da mercadoria - ver MapeadorProtocoloSd.Montar.
///
/// Existe porque o Guardian recusa o POST quando o emissor ou o item nao existem, e uma recusa no
/// envio e cara: ela custa uma ida a rede e chega como erro generico. Conferir antes transforma
/// isso em um motivo legivel na tela, com o codigo exato que falta cadastrar.
///
/// Deliberadamente NAO usa a consulta SOAP ConsultaPessoa: ela devolveu "nao existe" para emissores
/// que comprovadamente existiam (20000051 e 20000184), e uma verificacao que erra para o lado da
/// ausencia bloquearia linhas boas.
/// </summary>
public sealed record CadastroGuardianConhecido(
    IReadOnlyCollection<string> Emissores, IReadOnlyCollection<string> Itens)
{
    /// <summary>Vazio significa "nao sei": a conferencia nao acontece e a decisao fica com o Guardian.</summary>
    public static readonly CadastroGuardianConhecido Desconhecido = new([], []);

    public bool ConfereEmissor => Emissores.Count > 0;
    public bool ConfereItem => Itens.Count > 0;
    public bool TemEmissor(string codigo) => Emissores.Contains(codigo, StringComparer.Ordinal);
    public bool TemItem(string codigo) => Itens.Contains(codigo, StringComparer.Ordinal);
}

/// <summary>
/// Monta o protocolo de pre-cadastro SD a partir de UMA linha de ordem de venda.
///
/// Um protocolo por linha: duas linhas da mesma ordem com o mesmo material geram dois protocolos,
/// distinguidos pelo campo 1009. O material nao desempata nada; a linha sim.
/// </summary>
public static class MapeadorProtocoloSd
{
    /// <summary>
    /// camelCase, como o Guardian espera. Publico de proposito: a previsualizacao na tela e o
    /// envio tem de serializar pelo MESMO caminho, senao o operador confere um JSON e outro sai.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions JsonOpcoes =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Serializar(ProtocoloPreCadastroSdRequest pedido)
        => System.Text.Json.JsonSerializer.Serialize(pedido, JsonOpcoes);

    public static ProtocoloPreCadastroSdRequest Montar(
        OrdemVendaSdLeitura linha, string urlParaRetorno, CadastroGuardianConhecido? cadastro = null)
    {
        ArgumentNullException.ThrowIfNull(linha);
        cadastro ??= CadastroGuardianConhecido.Desconhecido;

        if (!Uri.TryCreate(urlParaRetorno, UriKind.Absolute, out var retorno)
            || retorno.Scheme != "https" || retorno.UserInfo.Length != 0)
            throw new ProtocoloSdBloqueadoException("SD_URL_RETORNO_INVALIDA",
                "A url de retorno precisa ser https e sem credencial embutida.");

        var item = CodigoGuardian(linha.Material, "SD_ITEM_AUSENTE", "material");

        // QUEM vai no protocolo e o RECEBEDOR DA MERCADORIA (funcao WE), nao o emissor da ordem
        // (funcao AG). Decisao do operador em 04/11/2026, depois da ordem 4274: ali os dois sao
        // diferentes, foi o emissor que viajou e estava errado.
        //
        // Faz sentido no processo: quem chega na balanca e quem recebe a mercadoria, e e o
        // cadastro dele que precisa existir no Guardian. Nas ordens em que emissor e recebedor
        // sao a mesma empresa - a maioria - a troca nao muda nada, e foi por isso que o erro
        // passou despercebido ate aparecer uma ordem em que eles diferem.
        //
        // O campo do Guardian continua se chamando "codigoEmissor": o nome e da API deles.
        var recebedor = CodigoGuardian(
            linha.RecebedorMercadoria, "SD_RECEBEDOR_AUSENTE", "recebedor da mercadoria");

        // Conferencia contra o que o operador ja cadastrou. Quando a lista esta vazia nao ha o que
        // conferir, e a decisao continua sendo do Guardian - nunca o contrario, que seria liberar
        // por falta de informacao.
        if (cadastro.ConfereItem && !cadastro.TemItem(item))
            throw new ProtocoloSdBloqueadoException("SD_ITEM_NAO_CADASTRADO",
                $"O item {item} nao consta no Guardian. Cadastre-o antes de criar o protocolo.");
        if (cadastro.ConfereEmissor && !cadastro.TemEmissor(recebedor))
            throw new ProtocoloSdBloqueadoException("SD_RECEBEDOR_NAO_CADASTRADO",
                $"O recebedor {recebedor} nao consta no Guardian. Cadastre-o antes de criar o protocolo.");

        var centro = Obrigatorio(linha.Centro, "SD_CENTRO_AUSENTE", "centro");
        if (!OrdensVendaSdFiltro.CentrosPermitidos.Contains(centro, StringComparer.Ordinal))
            throw new ProtocoloSdBloqueadoException("SD_CENTRO_FORA_DO_FLUXO",
                $"O centro {centro} nao pertence ao fluxo SD.");

        var ordem = CodigoGuardian(linha.CodigoOrdemVenda, "SD_ORDEM_AUSENTE", "ordem de venda");
        var item_linha = CodigoGuardian(linha.LinhaItem, "SD_LINHA_AUSENTE", "linha da ordem");

        return new ProtocoloPreCadastroSdRequest(
            new ConfiguracaoProtocoloSd(urlParaRetorno),
            new DadosProtocoloPreCadastroSd(
                // Zero sempre: quem numera o protocolo e o Guardian.
                Id: 0,
                CodigoItem: item,
                // Nome do campo e do Guardian; o valor e o recebedor da mercadoria.
                CodigoEmissor: recebedor,
                NumeroReferencia: Referencia(ordem, item_linha, linha.PedidoDoCliente),
                CamposAdicionais:
                [
                    new(CamposProtocoloSd.TipoDocumento, CamposProtocoloSd.Saida),
                    new(CamposProtocoloSd.Centro, centro),
                    new(CamposProtocoloSd.Operacao, CamposProtocoloSd.NaoSeAplica),
                    new(CamposProtocoloSd.OrdemVenda, ordem),
                    new(CamposProtocoloSd.Remessa, CamposProtocoloSd.NaoSeAplica),
                    new(CamposProtocoloSd.PedidoCompra, CamposProtocoloSd.NaoSeAplica),
                    new(CamposProtocoloSd.Lacre, CamposProtocoloSd.NaoSeAplica),
                    new(CamposProtocoloSd.Silo, CamposProtocoloSd.NaoSeAplica),
                    new(CamposProtocoloSd.Quadrante, CamposProtocoloSd.NaoSeAplica),
                    new(CamposProtocoloSd.LinhaDaOrdem, item_linha)
                ]));
    }

    /// <summary>
    /// Chave natural da linha, para nao criar o mesmo protocolo duas vezes. Usa os codigos JA
    /// normalizados: comparar a forma do SAP com a do Guardian foi exatamente o defeito que deixou
    /// a guarda de duplicidade do ACM sem efeito.
    /// </summary>
    public static string ChaveNatural(OrdemVendaSdLeitura linha)
    {
        ArgumentNullException.ThrowIfNull(linha);
        return CodigoGuardian(linha.CodigoOrdemVenda, "SD_ORDEM_AUSENTE", "ordem de venda")
            + "/" + CodigoGuardian(linha.LinhaItem, "SD_LINHA_AUSENTE", "linha da ordem");
    }

    /// <summary>
    /// Converte o valor do SAP no codigo do Guardian removendo os zeros a esquerda.
    ///
    /// Regra tirada do cadastro do proprio Guardian, conferido pelo operador em 31/10/2026:
    /// ITM_CODIGO guarda 7000002 e EMS_CODIGO guarda 20000610, enquanto o SAP entrega os mesmos
    /// valores preenchidos com zeros. Nada alem dos zeros e alterado - um codigo que nao seja so
    /// digitos passa inteiro, porque encurtar um codigo alfanumerico seria inventar outro.
    /// </summary>
    public static string Normalizar(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var limpo = valor.Trim();
        if (!limpo.All(char.IsDigit)) return limpo;
        var semZeros = limpo.TrimStart('0');
        // Um codigo que e so zeros nao vira vazio: vale "0".
        return semZeros.Length == 0 ? "0" : semZeros;
    }

    /// <summary>
    /// Referencia do protocolo: "OV" + ordem + "-" + linha + "-" + pedido do cliente.
    /// Formato combinado com o operador em 04/11/2026 - exemplo: OV4274-10-49580.
    ///
    /// Sem pedido do cliente a referencia PARA na linha ("OV4274-10"), em vez de terminar num
    /// hifen solto: nem toda ordem traz o pedido, e "OV4274-10-" na balanca parece dado perdido.
    ///
    /// Ordem e linha ja vem normalizadas, sem os zeros do SAP - a referencia mostra o que o
    /// operador le na tela do SAP, nao "OV0000004274-000010".
    /// </summary>
    public static string Referencia(string ordem, string linha, string? pedidoDoCliente)
    {
        var baseRef = "OV" + ordem + "-" + linha;
        var pedido = pedidoDoCliente?.Trim();
        return string.IsNullOrEmpty(pedido) ? baseRef : baseRef + "-" + pedido;
    }

    private static string CodigoGuardian(string? valor, string codigo, string campo)
        => Normalizar(Obrigatorio(valor, codigo, campo));

    private static string Obrigatorio(string? valor, string codigo, string campo)
        => string.IsNullOrWhiteSpace(valor)
            ? throw new ProtocoloSdBloqueadoException(codigo, $"A linha nao traz {campo}.")
            : valor.Trim();
}
