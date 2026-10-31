using FugaTES.Aplicacao.Modelos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Configuracao;

/// <summary>
/// R170: recorte continuo do cadastro ACM. Aplica a MESMA regra de negocio da janela de admissao
/// (<see cref="AcmInboundWindowOptions"/>), sem a caixa de tempo e sem o prefixo de correlacao.
///
/// Existe porque a janela so admite contratos que CHEGAM com ela aberta: o receptor SAP carimba a
/// correlacao com o prefixo da janela apenas quando roda em LoopbackBasicHttps, o que exigiria o
/// SAP entregar por HTTPS. Enquanto a entrega e HTTP, nenhuma janela pode admitir o que ja chegou.
///
/// Deliberadamente NAO tem lista de parceiros ou materiais permitidos. Quem decide se um contrato
/// pode ser cadastrado e o mapeamento de identificadores, que e fail-closed: contrato cujo parceiro
/// ou material ainda nao tem codigo Guardian aprovado entra no pipeline, e bloqueado antes do envio
/// e permanece visivel no FugaTES, em vez de ser recusado na porta de entrada ou - pior - cadastrado
/// com identificador errado.
/// </summary>
public sealed class AcmAutomaticoOptions
{
    public bool Enabled { get; set; }
    public string? AuthorizationReference { get; set; }
    public string? Centro { get; set; }
    public string? Item { get; set; }
    public string[] ExcludedContractNumbers { get; set; } = [];

    private static bool Token(string? value, int max) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() && value.Length <= max && !value.Any(char.IsControl);
    private static bool Identifier(string? value, int max) => Token(value, max) && value!.All(char.IsAsciiDigit);

    public bool EhValida() => Enabled &&
        Token(AuthorizationReference, 160) &&
        !new[] { "PENDENTE", "NOT_APPROVED", "NAO VALIDADO", "NÃO VALIDADO" }.Contains(AuthorizationReference, StringComparer.OrdinalIgnoreCase) &&
        Centro == "3011" && (Item is null || Identifier(Item, 10)) &&
        ExcludedContractNumbers is not null && ExcludedContractNumbers.All(v => Identifier(v, 20));

    public bool SnapshotPermitido(ContratoSapToledoSnapshot snapshot) => EhValida() &&
        Identifier(snapshot.NumeroContrato, 20) &&
        !AcmInboundWindowOptions.ContratoHistoricoOuExcluido(snapshot.NumeroContrato, ExcludedContractNumbers) &&
        snapshot.Centro == Centro && snapshot.Item == Item &&
        AcmInboundWindowOptions.EscopoContratoAprovado(snapshot.TipoContrato, snapshot.TipoPesagem) &&
        snapshot.StatusContrato == "T";
}
