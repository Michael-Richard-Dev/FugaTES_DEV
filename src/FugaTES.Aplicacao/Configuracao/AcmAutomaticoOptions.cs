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

    /// <summary>
    /// Centro unico do recorte. Mantido para a configuracao que ja existe em producao
    /// (<c>MotorIntegracao__AcmAutomatico__Centro=3011</c>) continuar valendo sem edicao.
    /// </summary>
    public string? Centro { get; set; }

    /// <summary>
    /// Centros do recorte, quando sao mais de um. Preenchido, substitui <see cref="Centro"/>.
    ///
    /// E lista, e nao um segundo recorte ligado em paralelo, porque <c>MotorIntegracaoOptions</c>
    /// recusa dois recortes ativos ao mesmo tempo: atender 3011 e 3008 tinha de caber DENTRO do
    /// recorte continuo, nao ao lado dele.
    /// </summary>
    public string[] Centros { get; set; } = [];

    public string? Item { get; set; }
    public string[] ExcludedContractNumbers { get; set; } = [];

    /// <summary>
    /// Corte do cadastro automatico: so contrato RECEBIDO a partir deste instante vira cadastro
    /// sozinho. Decisao do operador em 05/11/2026 - "daqui pra frente, dos proximos que vierem".
    ///
    /// E corte por data de RECEBIMENTO, e nao lista de numeros, porque o pedido era temporal: a
    /// fila tinha cerca de quarenta contratos travados em reconciliacao, em recusa 400 do Guardian
    /// e em aprovacao de callback, e enumera-los satisfaria hoje e exigiria manutencao amanha. O
    /// corte diz a regra uma vez. E o mesmo padrao que o ACM ja usa em
    /// DynamicEmitterValidationFromUtc e que o SD usa em OrdensVendaSdFiltro.CorteAutomatico.
    ///
    /// E instante FIXO, nunca "agora": com corte movel, o contrato que chegou hoje e ainda nao foi
    /// cadastrado sairia do recorte amanha, sem ninguem decidir isso.
    ///
    /// Nulo traz tudo - o comportamento anterior, preservado para quem nao configurar corte.
    ///
    /// Contrato que o SAP REENTREGAR depois do corte entra: o recebimento e novo, ainda que o
    /// numero seja antigo. E o que "os proximos que vierem" quer dizer.
    /// </summary>
    public DateTimeOffset? RecebidoDesdeUtc { get; set; }

    /// <summary>
    /// Se um recebimento esta dentro do corte.
    ///
    /// Recebimento NULO com corte configurado NAO passa: "nao sei quando chegou" nao pode valer
    /// como "chegou depois do corte" - seria deixar entrar justamente o que nao se consegue datar.
    /// </summary>
    public bool RecebimentoNoCorte(DateTimeOffset? recebidoEmUtc) =>
        RecebidoDesdeUtc is not { } corte || (recebidoEmUtc is { } quando && quando >= corte);

    /// <summary>
    /// Os centros que realmente valem. Vazio significa recorte INVALIDO - nunca "todos": sem
    /// centro declarado o motor nao sabe de quem e o contrato, e cadastro no Guardian nao se desfaz.
    /// </summary>
    public IReadOnlyList<string> CentrosEfetivos =>
        Centros is { Length: > 0 } ? Centros : Centro is null ? [] : [Centro];

    private static bool Token(string? value, int max) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() && value.Length <= max && !value.Any(char.IsControl);
    private static bool Identifier(string? value, int max) => Token(value, max) && value!.All(char.IsAsciiDigit);

    public bool EhValida() => Enabled &&
        Token(AuthorizationReference, 160) &&
        !new[] { "PENDENTE", "NOT_APPROVED", "NAO VALIDADO", "NÃO VALIDADO" }.Contains(AuthorizationReference, StringComparer.OrdinalIgnoreCase) &&
        CentrosEfetivos.Count > 0 && CentrosEfetivos.All(CentrosAcm.EhPermitido) &&
        CentrosEfetivos.Distinct(StringComparer.Ordinal).Count() == CentrosEfetivos.Count &&
        (Item is null || Identifier(Item, 10)) &&
        // Mesma exigencia dos outros cortes do ACM: instante declarado em UTC, nao em fuso local.
        (RecebidoDesdeUtc is null || RecebidoDesdeUtc.Value.Offset == TimeSpan.Zero) &&
        ExcludedContractNumbers is not null && ExcludedContractNumbers.All(v => Identifier(v, 20));

    public bool SnapshotPermitido(ContratoSapToledoSnapshot snapshot) => EhValida() &&
        Identifier(snapshot.NumeroContrato, 20) &&
        !AcmInboundWindowOptions.ContratoHistoricoOuExcluido(snapshot.NumeroContrato, ExcludedContractNumbers) &&
        snapshot.Centro is { } centro && CentrosEfetivos.Contains(centro, StringComparer.Ordinal) &&
        snapshot.Item == Item &&
        AcmInboundWindowOptions.EscopoContratoAprovado(snapshot.TipoContrato, snapshot.TipoPesagem) &&
        snapshot.StatusContrato == "T";
}
