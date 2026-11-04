namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// SD: uma linha de ordem de venda como o operador precisa ve-la, na granularidade de ITEM -
/// uma ordem com tres itens rende tres linhas. Os campos seguem a planilha de integracao do SAP,
/// com o nome do campo OData registrado ao lado para que a origem de cada valor seja rastreavel.
///
/// Leitura apenas: este modelo nao decide nada e nao e persistido. Serve para validar a busca no
/// SAP e acompanhar o desenvolvimento do fluxo.
/// </summary>
public sealed record OrdemVendaSdLeitura(
    // A_SalesOrderItem.ShippingPoint - "Centro" na planilha (3011 ou 3008).
    string? Centro,
    // A_SalesOrder.SalesOrder
    string CodigoOrdemVenda,
    // A_SalesOrderItem.Material - "Item da ordem" na planilha.
    string? Material,
    // A_SalesOrderItem.SalesOrderItem - "Linha do Item" na planilha.
    string? LinhaItem,
    // A_SalesOrder.OverallSDProcessStatus - o recorte da varredura. So "A" entra.
    string? SituacaoOrdem,
    // A_SalesOrder.SalesOrderType - o filtro "so o que e pra balanca" age aqui.
    string? TipoDocumentoVendas,
    // A_SalesOrder.SalesOrganization (3000)
    string? EmpresaOrdem,
    // A_SalesOrderHeaderPartner.Customer do parceiro cuja funcao e a de emissor.
    string? Emissor,
    // A_SalesOrderItem.OrganizationDivision
    string? SetorAtividade,
    // A_SalesOrderHeaderPartner.Customer do parceiro cuja funcao e a de recebedor da mercadoria.
    string? RecebedorMercadoria,
    // Projecao dos campos desta linha, com os nomes OData de origem, para conferencia na tela.
    // NAO e a resposta bruta: a ordem inteira trazia todos os itens, todos os parceiros e os
    // metadados do servico, o que escondia o que interessa. Nada aqui e interpretado.
    string PayloadJson)
{
    /// <summary>
    /// A_SalesOrder.CreationDate. E o que separa o acervo antigo do que entrou depois do corte:
    /// o motor automatico so cria protocolo para ordens criadas a partir da data combinada.
    ///
    /// Nula quando o servico nao devolveu a data. Nula NAO passa no corte - deixar passar seria
    /// tratar "nao sei quando foi criada" como "foi criada hoje".
    /// </summary>
    public DateTime? DataCriacao { get; init; }

    /// <summary>
    /// A_SalesOrderItem.PurchaseOrderByCustomer - o numero do pedido NO CLIENTE, como ele mesmo
    /// o chama (exemplo: "49580"). Pedido do operador em 04/11/2026.
    ///
    /// E campo de ITEM no servico, nao de cabecalho: itens diferentes da mesma ordem podem trazer
    /// pedidos diferentes. Por isso viaja na linha, e nao na ordem.
    ///
    /// Nulo quando a ordem nao traz: e informacao do cliente, nem toda ordem tem. Nulo aqui nao
    /// bloqueia nada - e um dado a mais na tela, nao um requisito do fluxo.
    /// </summary>
    public string? PedidoDoCliente { get; init; }
}

/// <summary>
/// Recorte da varredura. Sem valores aqui a busca nao acontece: uma varredura a cada 30 segundos
/// sem filtro traria a base de vendas inteira do SAP.
/// </summary>
public sealed record OrdensVendaSdFiltro(
    // Fixa em OrganizacaoFixa; qualquer outra reprova o filtro.
    string SalesOrganization,
    // Um ou mais de CentrosPermitidos, sem repeticao. Nenhum outro centro e aceito.
    IReadOnlyList<string> ShippingPoints,
    // Vazio significa OS CINCO TIPOS DO FLUXO (ver TiposPermitidos), nao "todos". Um subconjunto
    // deles tambem e aceito, para o operador estreitar a busca na tela; qualquer tipo de fora
    // reprova o filtro.
    IReadOnlyList<string> SalesOrderTypes,
    int MaximoPorVarredura = 200,
    // A_SalesOrder.OverallSDProcessStatus exigido - "Situacao da Ordem" na planilha. Decisao do
    // operador em 31/10/2026: a varredura so considera "A". Fica como parametro para nao espalhar
    // o literal, mas nao e editavel na tela - e regra, nao filtro de conveniencia.
    string SituacaoOrdemAprovada = "A",
    // Funcoes de parceiro em A_SalesOrderHeaderPartner. Os padroes sao os do SAP: AG para quem
    // emite a ordem e WE para quem recebe a mercadoria. Ficam configuraveis porque nao foram
    // confirmados pela planilha - ela nomeia o campo, nao o valor da funcao.
    string FuncaoEmissor = "AG",
    string FuncaoRecebedor = "WE")
{
    /// <summary>Organizacao de vendas da Fuga. Decisao do operador em 31/10/2026: nao e escolha.</summary>
    public const string OrganizacaoFixa = "3000";

    /// <summary>Unicos centros do fluxo SD. A tela escolhe entre eles; nenhum outro e aceito.</summary>
    public static readonly string[] CentrosPermitidos = ["3011", "3008"];

    /// <summary>
    /// Unicos tipos de documento de venda que pertencem ao fluxo da balanca. Lista fechada pelo
    /// operador em 04/11/2026, depois de confirmada com o negocio - antes disso a varredura
    /// trazia TODOS os tipos, porque a planilha de integracao dizia "puxar so o que e pra
    /// balanca" sem dizer quais.
    ///
    /// E regra, como os centros, e nao conveniencia de tela: uma ordem de outro tipo nao vai a
    /// balanca, e trazer o que nao vai a balanca so enche a lista do operador e gasta varredura.
    /// Por isso tipo de fora REPROVA o filtro, em vez de ser ignorado em silencio.
    /// </summary>
    public static readonly string[] TiposPermitidos = ["ZVSP", "ZVBI", "ZVSC", "ZRMS", "ZVNE"];

    /// <summary>
    /// Os tipos que realmente vao ao SAP. Vazio significa os cinco - nunca "todos": antes, lista
    /// vazia fazia a clausula sumir do $filter e a varredura trazia a base de vendas inteira.
    /// </summary>
    public IReadOnlyList<string> TiposEfetivos =>
        SalesOrderTypes is { Count: > 0 } ? SalesOrderTypes : TiposPermitidos;

    /// <summary>
    /// Corte do motor automatico: so ordens criadas a partir daqui viram protocolo sozinhas.
    ///
    /// Decisao do operador em 01/11/2026. E data FIXA, nao "hoje": com "hoje" movel, amanha o
    /// motor deixaria de ver as ordens criadas hoje que ainda nao foram pesadas. O acervo antigo
    /// (268 ordens em situacao A, muitas de anos atras) fica de fora para sempre - elas nao vao
    /// a balanca, e protocolo criado nao se desfaz.
    ///
    /// Vale so para o que acontece SOZINHO. O envio manual pela tela continua alcancando
    /// qualquer linha.
    /// </summary>
    public static readonly DateOnly CorteAutomatico = new(2026, 11, 1);

    /// <summary>
    /// Data minima de criacao da ordem. Nula traz todas - e o que a tela usa, para o operador
    /// enxergar tambem o que o motor nao vai tocar.
    /// </summary>
    public DateOnly? DataCriacaoMinima { get; init; }

    // A regra mora aqui, e nao so na tela: um filtro montado em codigo com outro centro ou outra
    // organizacao tambem tem de ser recusado, senao a tela seria a unica garantia.
    public bool EhValido() =>
        SalesOrganization == OrganizacaoFixa &&
        ShippingPoints is { Count: > 0 } &&
        ShippingPoints.Distinct(StringComparer.Ordinal).Count() == ShippingPoints.Count &&
        ShippingPoints.All(c => CentrosPermitidos.Contains(c, StringComparer.Ordinal)) &&
        SalesOrderTypes is not null && SalesOrderTypes.Count <= TiposPermitidos.Length &&
        SalesOrderTypes.All(Token) &&
        SalesOrderTypes.All(t => TiposPermitidos.Contains(t, StringComparer.Ordinal)) &&
        SalesOrderTypes.Distinct(StringComparer.Ordinal).Count() == SalesOrderTypes.Count &&
        Token(FuncaoEmissor) && Token(FuncaoRecebedor) && Token(SituacaoOrdemAprovada) &&
        MaximoPorVarredura is > 0 and <= 1000;

    private static bool Token(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() && value.Length <= 20 && value.All(char.IsLetterOrDigit);
}
