using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// SD: protocolo de pre-cadastro no Guardian, um por LINHA de ordem de venda.
///
/// Tipos proprios, e nao os do ACM, por um motivo concreto: o ACM exige exatamente tres campos
/// adicionais (1000 tipo de contrato, 1002 centro, 1003 operacao) e valida seus valores. O SD usa
/// dez campos e os mesmos numeros significam outra coisa - no SD, 1000 e tipo de documento e 1001
/// e o centro. Afrouxar a validacao do ACM para caber o SD enfraqueceria um caminho que hoje
/// registra contrato em producao.
/// </summary>
public sealed record DadosProtocoloPreCadastroSd(
    // Sempre 0: o Guardian atribui e incrementa o id do protocolo.
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("codigoItem")] string CodigoItem,
    [property: JsonPropertyName("codigoEmissor")] string CodigoEmissor,
    // Referencia legivel do protocolo, no formato combinado com o operador em 04/11/2026:
    // "OV" + ordem + "-" + linha + "-" + pedido do cliente (ex.: OV4274-10-49580). E o que
    // identifica o protocolo para quem olha a balanca, onde nao ha a tela do SAP ao lado.
    [property: JsonPropertyName("numeroReferencia")] string NumeroReferencia,
    [property: JsonPropertyName("camposAdicionais")] IReadOnlyList<CampoAdicionalGuardianSet26> CamposAdicionais);

public sealed record ConfiguracaoProtocoloSd(
    [property: JsonPropertyName("urlParaRetorno")] string UrlParaRetorno);

public sealed record ProtocoloPreCadastroSdRequest(
    [property: JsonPropertyName("configuracao")] ConfiguracaoProtocoloSd Configuracao,
    [property: JsonPropertyName("dadosProtocoloPreCadastro")] DadosProtocoloPreCadastroSd DadosProtocoloPreCadastro);

/// <summary>Numeros dos campos adicionais do fluxo SD, conforme o mapeamento do operador.</summary>
public static class CamposProtocoloSd
{
    public const int TipoDocumento = 1000;
    public const int Centro = 1001;
    public const int Operacao = 1002;
    public const int OrdemVenda = 1003;
    public const int Remessa = 1004;
    public const int PedidoCompra = 1005;
    public const int Lacre = 1006;
    public const int Silo = 1007;
    public const int Quadrante = 1008;
    public const int LinhaDaOrdem = 1009;

    // NAO HA LISTA DE CAMPOS ADICIONAIS DE LACRE.
    //
    // Houve, entre 05/11/2026 e o mesmo dia: [1006, 1010, 1011, 1012, 1013]. O operador decidiu
    // que o lacre vem da "Observacao" e que esses numeros nao serao mais usados, depois de a
    // pesagem 0030384 mostrar os lacres justamente la e nenhum 1010-1013 na devolutiva.
    //
    // O 1006 (<see cref="Lacre"/>) CONTINUA EXISTINDO e continua sendo enviado por nos como "0"
    // no protocolo de pre-cadastro - naquele momento o lacre ainda nao existe. O que saiu foi a
    // LEITURA por numero, nao o campo.
    //
    // ATENCAO ao reaproveitar 1010 a 1013: eles ja foram reservados duas vezes e abandonados nas
    // duas (transportadora/carreta, depois lacre). Quem for usa-los comeca de um numero sem
    // histórico de leitura, e nao de um que ja significou outra coisa.

    /// <summary>Os dez campos, na ordem do mapeamento. Nem um a mais, nem um a menos.</summary>
    public static readonly int[] Todos =
        [TipoDocumento, Centro, Operacao, OrdemVenda, Remessa,
         PedidoCompra, Lacre, Silo, Quadrante, LinhaDaOrdem];

    /// <summary>Venda e saida. Entrada ("E") existe no dominio do Guardian, nao neste fluxo.</summary>
    public const string Saida = "S";

    /// <summary>
    /// Campos que o fluxo SD nao preenche. Vao como texto "0" e nao como numero porque o campo
    /// "valor" do Guardian e string - mandar numero mudaria o tipo no wire.
    /// </summary>
    public const string NaoSeAplica = "0";
}

/// <summary>Motivo pelo qual uma linha nao virou protocolo. Nenhuma linha e enviada "no escuro".</summary>
public sealed class ProtocoloSdBloqueadoException(string codigo, string detalhe)
    : Exception(codigo + ": " + detalhe)
{
    public string Codigo { get; } = codigo;
    public string Detalhe { get; } = detalhe;
}
