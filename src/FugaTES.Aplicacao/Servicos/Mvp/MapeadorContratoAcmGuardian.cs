using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed record CamposAcmGuardianConfirmados(string NumeroContratoSap, string NumeroReferencia,
    string Estado, string TipoLancamento, string CodigoFluxo);

public sealed record RegraValorProximidadeFimAcm(decimal? ValorAprovado, string? EscopoOperacional = null)
{
    public decimal Resolver(string fluxo, string? centro)
    {
        if (fluxo != "ACM_ENTRADA" || !CentrosAcm.EhPermitido(centro))
            throw new DecisaoFuncionalPendenteSapException("ACM_VALOR_PROXIMIDADE_FIM");
        if (ValorAprovado is not null)
            return ValorAprovado >= 0m ? ValorAprovado.Value
                : throw new DecisaoFuncionalPendenteSapException("ACM_VALOR_PROXIMIDADE_FIM");
        // R6: temporary HML/MVP assumption; follow-up owner ARGOS / BRUNO / TOLEDO.
        if (string.Equals(EscopoOperacional, "HML", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(EscopoOperacional, "MVP", StringComparison.OrdinalIgnoreCase))
            return 0m;
        throw new DecisaoFuncionalPendenteSapException("ACM_VALOR_PROXIMIDADE_FIM");
    }
}

// Functional approval is bound to one immutable SAP contract version; it is not runtime write authorization.
public sealed record DecisaoContratoAcmGuardianComprovada(string NumeroContratoSap, long VersaoH4,
    string CodigoGuardian, string Estado, string TipoLancamento, string FonteAprovacao);

public sealed record TransportadoraAcmComprovada(string Codigo, string FonteTransacional);

public sealed class MapeadorContratoAcmGuardian(RegraValorProximidadeFimAcm? regra = null) : IMapeadorContratoAcmGuardian
{
    public static decimal ResolverValorProximidadeFim(VersaoContratoSapToledo versao, decimal? valorAprovado = null, string? escopoOperacional = null)
    {
        ArgumentNullException.ThrowIfNull(versao);
        if (versao.IntegracaoId == Guid.Empty || versao.NumeroContrato != versao.Snapshot.NumeroContrato)
            throw new DecisaoFuncionalPendenteSapException("ACM_CONTRACT_VERSION_IDENTITY_INVALID");
        _ = MapearCamposConfirmados(versao.Snapshot);
        return new RegraValorProximidadeFimAcm(valorAprovado, escopoOperacional).Resolver("ACM_ENTRADA", versao.Snapshot.Centro);
    }

    public static ContratoGuardianSet26 AplicarValorProximidadeFim(VersaoContratoSapToledo versao,
        ContratoGuardianSet26 comando, decimal? valorAprovado = null)
    {
        ArgumentNullException.ThrowIfNull(comando);
        return comando with { ValorProximidadeFimContrato = ResolverValorProximidadeFim(versao, valorAprovado) };
    }

    public ContratoGuardianSet26 MapearComDecisaoComprovada(VersaoContratoSapToledo versao,
        DecisaoContratoAcmGuardianComprovada decisao)
    {
        ArgumentNullException.ThrowIfNull(versao); ArgumentNullException.ThrowIfNull(decisao);
        if (versao.IntegracaoId == Guid.Empty || versao.NumeroContrato != versao.Snapshot.NumeroContrato ||
            decisao.NumeroContratoSap != versao.NumeroContrato || decisao.VersaoH4 != versao.Versao ||
            !CentrosAcm.EhPermitido(versao.Snapshot.Centro) || versao.Snapshot.TipoContrato != "AC3P" ||
            new[] { decisao.CodigoGuardian, decisao.Estado, decisao.TipoLancamento, decisao.FonteAprovacao }
                .Any(v => string.IsNullOrWhiteSpace(v) || new[] { "NÃO VALIDADO", "NAO VALIDADO", "PENDENTE" }.Contains(v.Trim(), StringComparer.OrdinalIgnoreCase)))
            throw new DecisaoFuncionalPendenteSapException("ACM_CONTRACT_FUNCTIONAL_APPROVAL_REQUIRED");
        var snapshot = versao.Snapshot;
        if (snapshot.QuantidadeContrato is null || snapshot.QuantidadeContrato < 0 || string.IsNullOrWhiteSpace(snapshot.DescricaoTipoContrato) ||
            new[] { "NÃO VALIDADO", "NAO VALIDADO", "PENDENTE" }.Contains(snapshot.DescricaoTipoContrato.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new DecisaoFuncionalPendenteSapException("ACM_CONTRACT_QUANTITY_DESCRIPTION_REQUIRED");
        var proximidade = (regra ?? new RegraValorProximidadeFimAcm(null)).Resolver("ACM_ENTRADA", snapshot.Centro);
        // Quantity/peso is never substituted by ValorUnitario. Approved identifiers are copied verbatim, no suffix.
        return new(decisao.CodigoGuardian, snapshot.DescricaoTipoContrato, snapshot.QuantidadeContrato.Value,
            decisao.Estado, proximidade, decisao.TipoLancamento)
        { CodigoEmissor = snapshot.IdParceiro, CodigoItem = snapshot.Material };
    }

    public ContratoGuardianSet26 Mapear(VersaoContratoSapToledo versao)
    {
        _ = ResolverValorProximidadeFim(versao, regra?.ValorAprovado, regra?.EscopoOperacional);
        return Mapear(versao.Snapshot);
    }

    public static CamposAcmGuardianConfirmados MapearCamposConfirmados(ContratoSapToledoSnapshot contrato)
    {
        ArgumentNullException.ThrowIfNull(contrato);
        // R170: compra e venda sao aceitas. O sentido do lancamento e decidido pelo par tipo de
        // contrato + tipo de pesagem, a mesma regra de AcmInboundWindowOptions.EscopoContratoAprovado.
        // A operacao do SAP nao participa: ela varia para o mesmo sentido de negocio (10 e 30 sao
        // ambos compra) e nao acrescenta informacao sobre credito ou debito.
        if (!CentrosAcm.EhPermitido(contrato.Centro) || contrato.TipoContrato is not ("AC3P" or "AC3S"))
            throw new DecisaoFuncionalPendenteSapException("ACM_SCENARIO_NOT_APPROVED");
        var estado = contrato.StatusContrato == "T" ? "308102"
            : throw new DecisaoFuncionalPendenteSapException("ACM_ESTADO");
        // Compra credita saldo; venda debita. O sentido ja esta no tipo de contrato, e e so disso
        // que este mapeador precisa. Exigir aqui tambem a pesagem endureceria um caminho que nunca
        // dependeu dela e quebraria chamadores legitimos; quem exige o par fechado e o escopo
        // (janela R57, AcmAutomatico, o gate de preparacao e o predicado SQL do motor).
        var tipoLancamento = contrato.TipoContrato switch
        {
            "AC3P" => "309837",
            "AC3S" => "309838",
            _ => throw new DecisaoFuncionalPendenteSapException("ACM_TIPO_LANCAMENTO")
        };
        return new(contrato.NumeroContrato, GerarReferencia(contrato.NumeroContrato), estado,
            tipoLancamento, "FLX_RECEBIMENTO");
    }

    public static async Task ExigirReferenciaDisponivelAsync(CamposAcmGuardianConfirmados campos,
        IRepositorioCorrelacaoRecebimento correlacoes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campos);
        ArgumentNullException.ThrowIfNull(correlacoes);
        ct.ThrowIfCancellationRequested();
        if (campos.NumeroReferencia != GerarReferencia(campos.NumeroContratoSap))
            throw new DecisaoFuncionalPendenteSapException("ACM_REFERENCE_INVALID");
        // An existing reference cannot prove the immutable H4 version link yet.
        if (await correlacoes.ObterPorDocumentoAsync(campos.NumeroReferencia, ct) is not null)
            throw new DecisaoFuncionalPendenteSapException("ACM_REFERENCE_COLLISION_OR_UNPROVEN_LINK");
    }

    public ContratoGuardianSet26 Mapear(ContratoSapToledoSnapshot contrato)
    {
        var campos = MapearCamposConfirmados(contrato);
        var proximidade = (regra ?? new RegraValorProximidadeFimAcm(null)).Resolver("ACM_ENTRADA", contrato.Centro);
        if (contrato.QuantidadeContrato is null || contrato.QuantidadeContrato < 0 ||
            new[] { contrato.DescricaoTipoContrato, contrato.IdParceiro, contrato.Material }.Any(string.IsNullOrWhiteSpace))
            throw new DecisaoFuncionalPendenteSapException("ACM_CONTRATO_GUARDIAN_MAPPING");
        return new(contrato.NumeroContrato, contrato.DescricaoTipoContrato!, contrato.QuantidadeContrato.Value,
            campos.Estado, proximidade, campos.TipoLancamento)
        { CodigoEmissor = contrato.IdParceiro, CodigoItem = contrato.Material };
    }

    public static PreCadastroRecebimentoGuardianSet26 MapearPreCadastro(ContratoSapToledoSnapshot contrato,
        TransportadoraAcmComprovada? transportadora, string urlParaRetorno,
        bool transportadoraPreenchidaNoGuardian = false)
    {
        // Explicit functional decision: the agent completes the carrier in Guardian.
        // Existing callers retain the previous fail-closed default; no BP fallback.
        if ((transportadora is null && !transportadoraPreenchidaNoGuardian) ||
            (transportadora is not null && (string.IsNullOrWhiteSpace(transportadora.Codigo) ||
            string.IsNullOrWhiteSpace(transportadora.FonteTransacional))))
            throw new DecisaoFuncionalPendenteSapException("ACM_CARRIER_SOURCE");
        if (transportadoraPreenchidaNoGuardian && transportadora is not null)
            throw new DecisaoFuncionalPendenteSapException("ACM_CARRIER_COMPLETION_AMBIGUOUS");
        var campos = MapearCamposConfirmados(contrato);
        if (string.IsNullOrWhiteSpace(contrato.Material) || string.IsNullOrWhiteSpace(contrato.IdParceiro))
            throw new DecisaoFuncionalPendenteSapException("ACM_PRECADASTRO_MAPPING");
        return new(0, campos.CodigoFluxo, contrato.Material, contrato.IdParceiro,
            transportadora?.Codigo, contrato.NumeroContrato, 1, campos.NumeroReferencia, urlParaRetorno,
            [new(1000, contrato.TipoContrato!), new(1002, contrato.Centro!), new(1003, contrato.OperacaoContrato!)])
        { TransportadoraPreenchidaNoGuardian = transportadoraPreenchidaNoGuardian };
    }

    private static string GerarReferencia(string numeroContrato)
    {
        if (string.IsNullOrEmpty(numeroContrato) || numeroContrato.Any(c => c is < '0' or > '9'))
            throw new DecisaoFuncionalPendenteSapException("ACM_REFERENCE_INVALID");
        var numero = numeroContrato.TrimStart('0');
        if (numero.Length == 0)
            throw new DecisaoFuncionalPendenteSapException("ACM_REFERENCE_INVALID");
        var referencia = "OC" + numero;
        if (referencia.Length > 10)
            throw new DecisaoFuncionalPendenteSapException("ACM_REFERENCE_MAX_LENGTH");
        return referencia;
    }
}
