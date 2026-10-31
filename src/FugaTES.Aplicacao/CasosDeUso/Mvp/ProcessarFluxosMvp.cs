using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.Aplicacao.Servicos.Mvp;

namespace FugaTES.Aplicacao.CasosDeUso.Mvp;

public sealed class ProcessarSaidaSd(
    IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados, IDecisoesFluxoMvp decisoes, TimeProvider tempo,
    IConsultaOrdemVendaSap ordens, IConsultaPartnerSap partners, IConsultaMaterialSap materiais,
    IRemessaSap remessas, IDecisaoRemessaSap decisaoRemessa, IPreparacaoSaidaSdMvp preparacao,
    IPreCadastroGuardianSet26Gateway guardian)
    : FluxoMvpCasoDeUso(FluxoMvp.SdOut, motor, resultados, decisoes, tempo)
{
    protected override async Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
    {
        var ordem = ExigirConsulta(await ordens.ConsultarAsync(claim.ChaveNegocio, ct));
        if (ordem.SalesOrder != claim.ChaveNegocio) throw new InvalidOperationException("SD_IDENTITY_MISMATCH");
        efeitos.Contexto = efeitos.Contexto with { SalesOrder = ordem.SalesOrder };
        var codigoContrato = RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(ordem);
        var partnerId = RegrasFuncionaisMvpConfirmadas.ResolverEmissor(ordem);
        if (string.IsNullOrWhiteSpace(partnerId)) throw new CapabilityMvpPendenteException("SD_PARTNER_MAPPING");
        var partner = ExigirConsulta(await partners.ConsultarAsync(partnerId, ct));
        if (partner.BusinessPartner != partnerId || partner.Blocked) throw new CapabilityMvpPendenteException("SD_PARTNER_INVALID");
        var transportadoraId = RegrasFuncionaisMvpConfirmadas.ResolverTransportadora(ordem);
        var transportadora = ExigirConsulta(await partners.ConsultarAsync(transportadoraId, ct));
        RegrasFuncionaisMvpConfirmadas.ValidarTransportadora(transportadoraId, transportadora);
        var materialId = preparacao.ResolverMaterial(ordem);
        var material = materialId is null ? null : ExigirConsulta(await materiais.ConsultarAsync(materialId, ct));
        if (material is not null && material.Product != materialId) throw new CapabilityMvpPendenteException("SD_PRODUCT_INVALID");
        var consultaRemessa = await remessas.ObterCandidatasPorOrdemVendaAsync(ordem.SalesOrder, ct);
        var candidatas = consultaRemessa.Status == StatusConsultaSap.NaoEncontrado ? [] : ExigirConsulta(consultaRemessa);
        var remessa = preparacao.SelecionarRemessa(ordem, candidatas);
        // Preserve constructor compatibility; Delivery writes are not part of initial SD scheduling.
        _ = decisaoRemessa;
        var preCadastro = preparacao.MapearGuardian(ordem, partner, material);
        if (preCadastro.CodigoEmissor != partnerId || preCadastro.CodigoTransportadora != transportadoraId ||
            preCadastro.CodigoContrato != codigoContrato)
            throw new DecisaoFuncionalPendenteSapException("SD_GUARDIAN_MAPPING_CONFLICT");
        ValidarGuardian(preCadastro);
        efeitos.Contexto = efeitos.Contexto with { DeliveryDocument = remessa?.DeliveryDocument };
        var protocolo = await efeitos.ExecutarAsync(() => guardian.EnviarAsync(preCadastro, ct));
        if (string.IsNullOrWhiteSpace(protocolo.Valor)) throw new InvalidOperationException("GUARDIAN_PROTOCOL_MISSING");
        return new(StatusFluxoMvp.AguardandoCallback, claim.CorrelationId, Operacao,
            SalesOrder: ordem.SalesOrder, DeliveryDocument: remessa?.DeliveryDocument,
            ProtocoloGuardian: protocolo.Valor, NumeroTicketGuardian: protocolo.NumeroTicket);
    }
}

public sealed class ProcessarEntradaMm(
    IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados, IDecisoesFluxoMvp decisoes, TimeProvider tempo,
    IConsultaPedidoCompraSap pedidos, IConsultaPartnerSap partners, IConsultaMaterialSap materiais,
    IPreparacaoEntradaMmMvp preparacao, IPreCadastroGuardianSet26Gateway guardian)
    : FluxoMvpCasoDeUso(FluxoMvp.MmInPreCadastro, motor, resultados, decisoes, tempo)
{
    protected override async Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
    {
        var pedido = ExigirConsulta(await pedidos.ConsultarAsync(claim.ChaveNegocio, ct));
        if (pedido.PurchaseOrder != claim.ChaveNegocio) throw new InvalidOperationException("MM_IDENTITY_MISMATCH");
        efeitos.Contexto = efeitos.Contexto with { PurchaseOrder = pedido.PurchaseOrder };
        var codigoContrato = RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(pedido);
        var supplierId = preparacao.ResolverSupplier(pedido);
        var materialId = preparacao.ResolverMaterial(pedido);
        if (string.IsNullOrWhiteSpace(supplierId) || string.IsNullOrWhiteSpace(materialId))
            throw new CapabilityMvpPendenteException("MM_DEPENDENCY_MAPPING");
        var supplier = ExigirConsulta(await partners.ConsultarAsync(supplierId, ct));
        var material = ExigirConsulta(await materiais.ConsultarAsync(materialId, ct));
        if (supplier.BusinessPartner != supplierId || supplier.Blocked || material.Product != materialId)
            throw new CapabilityMvpPendenteException("MM_DEPENDENCY_INVALID");
        var preCadastro = preparacao.MapearGuardian(pedido, supplier, material);
        if (preCadastro.CodigoContrato != codigoContrato)
            throw new DecisaoFuncionalPendenteSapException("MM_GUARDIAN_MAPPING_CONFLICT");
        ValidarGuardian(preCadastro);
        var protocolo = await efeitos.ExecutarAsync(() => guardian.EnviarAsync(preCadastro, ct));
        if (string.IsNullOrWhiteSpace(protocolo.Valor)) throw new InvalidOperationException("GUARDIAN_PROTOCOL_MISSING");
        return new(StatusFluxoMvp.AguardandoCallback, claim.CorrelationId, Operacao,
            PurchaseOrder: pedido.PurchaseOrder, ProtocoloGuardian: protocolo.Valor,
            NumeroTicketGuardian: protocolo.NumeroTicket);
    }
}

public sealed class ProcessarMovimento101Mm(
    IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados, IDecisoesFluxoMvp decisoes, TimeProvider tempo,
    IRepositorioCorrelacaoRecebimento correlacoes, IConsultaPedidoCompraSap pedidos,
    IGatilhoMovimentoMaterialSap gatilho, IMovimentoMaterialSap movimentos)
    : FluxoMvpCasoDeUso(FluxoMvp.MmIn101, motor, resultados, decisoes, tempo)
{
    protected override async Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
    {
        var correlacao = await correlacoes.ObterPorDocumentoAsync(claim.ChaveNegocio, ct);
        if (correlacao is null || correlacao.DocumentoSap != claim.ChaveNegocio || string.IsNullOrWhiteSpace(correlacao.TicketGuardian) ||
            correlacao.Pesos is null || string.IsNullOrWhiteSpace(correlacao.Entrada.PedidoCompras))
            throw new CapabilityMvpPendenteException("MM_FINAL_WEIGHING_MISSING");
        var pedido = ExigirConsulta(await pedidos.ConsultarAsync(correlacao.Entrada.PedidoCompras, ct));
        if (pedido.PurchaseOrder != correlacao.Entrada.PedidoCompras) throw new CapabilityMvpPendenteException("MM_PURCHASE_ORDER_MISMATCH");
        efeitos.Contexto = efeitos.Contexto with { PurchaseOrder = pedido.PurchaseOrder };
        if (!gatilho.DeveLancar(pedido))
            return new(StatusFluxoMvp.Concluido, claim.CorrelationId, "MM_101_NO_ACTION", PurchaseOrder: pedido.PurchaseOrder);
        var comando = gatilho.Mapear(pedido);
        if (comando.Payload.Count == 0) throw new CapabilityMvpPendenteException("MM_MOVIMENTO_101_MAPPING");
        var resultado = ExigirEscrita(await efeitos.ExecutarAsync(() => movimentos.LancarAsync(comando, ct)));
        return new(StatusFluxoMvp.Concluido, claim.CorrelationId, Operacao,
            PurchaseOrder: pedido.PurchaseOrder, MaterialDocument: resultado.MaterialDocument,
            MaterialDocumentYear: resultado.MaterialDocumentYear);
    }
}

public sealed class ProcessarContratoAcm(
    IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados, IDecisoesFluxoMvp decisoes, TimeProvider tempo,
    IContratoSapToledoVersaoStore contratos, IMapeadorContratoAcmGuardian mapper, ICadastraContratoGuardianGateway guardian,
    IRepositorioCorrelacaoRecebimento? correlacoes = null, IAcmGuardianVinculoStore? vinculos = null,
    IResolverTransportadoraAcm? transportadoras = null, AcmR57Pipeline? pipelineR57 = null)
    : FluxoMvpCasoDeUso(FluxoMvp.AcmInContrato, motor, resultados, decisoes, tempo)
{
    public const string OperacaoInboundH4 = "SAP_CONTRATO_TOLEDO";
    // R57 owns its fenced per-stage journal. The generic final upsert must not overwrite an armed marker.
    public override Task<ResultadoFluxoMvp> ExecutarAsync(ClaimIntegracaoMotor claim,CancellationToken ct)
        => pipelineR57 is null?base.ExecutarAsync(claim,ct):pipelineR57.ExecutarAsync(claim,ct);


    protected override async Task ExigirDecisoesAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
    {
        if(pipelineR57 is not null)return;
        var inboundId = claim.IntegracaoId;
        if (claim.Operacao != OperacaoInboundH4 && !Guid.TryParse(claim.ChaveNegocio, out inboundId))
            throw new CapabilityMvpPendenteException("ACM_DURABLE_INBOUND_MISSING");
        var versao = await contratos.ObterPorIntegracaoIdAsync(inboundId, ct)
            ?? throw new CapabilityMvpPendenteException("ACM_DURABLE_INBOUND_MISSING");
        if (versao.Snapshot.Centro != "3011" || versao.Snapshot.TipoContrato != "AC3P")
        {
            await base.ExigirDecisoesAsync(claim, ct);
            return;
        }
        var campos = MapeadorContratoAcmGuardian.MapearCamposConfirmados(versao.Snapshot);
        if (correlacoes is not null)
            await MapeadorContratoAcmGuardian.ExigirReferenciaDisponivelAsync(campos, correlacoes, ct);
        _ = mapper.Mapear(versao);
        // H4 keeps the already approved funcional decisions; persistence is reserved before dispatch.
    }

    protected override async Task<bool> ValidarClaimAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
    {
        if (claim.Operacao != OperacaoInboundH4) return await base.ValidarClaimAsync(claim, ct);
        if (claim.Origem != "SAP" || claim.Destino != "TOLEDO") return false;
        // H4 owns the fingerprint and links the immutable version to the original INC005 id.
        var versao = await contratos.ObterPorIntegracaoIdAsync(claim.IntegracaoId, ct);
        return versao is not null && versao.IntegracaoId == claim.IntegracaoId &&
            versao.NumeroContrato == claim.ChaveNegocio && versao.Snapshot.NumeroContrato == versao.NumeroContrato &&
            versao.FingerprintSha256 == claim.FingerprintSha256 && versao.Snapshot.Centro == "3011";
    }

    protected override async Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
    {
        if(pipelineR57 is not null)return await pipelineR57.ExecutarAsync(claim,ct,efeitos.MarcarEnvioIniciado);
        var inboundId = claim.IntegracaoId;
        if (claim.Operacao != OperacaoInboundH4 && !Guid.TryParse(claim.ChaveNegocio, out inboundId))
            throw new InvalidOperationException("ACM_INBOUND_ID_INVALID");
        var versao = await contratos.ObterPorIntegracaoIdAsync(inboundId, ct)
            ?? throw new CapabilityMvpPendenteException("ACM_DURABLE_INBOUND_MISSING");
        if (await contratos.TemPredecessorNaoTerminalAsync(versao.NumeroContrato, versao.Versao, ct))
            throw new CapabilityMvpPendenteException("ACM_PREDECESSOR_PENDING");
        var comando = mapper.Mapear(versao);

        if (new[] { comando.Codigo, comando.Descricao, comando.Estado, comando.TipoLancamento }.Any(string.IsNullOrWhiteSpace))
            throw new CapabilityMvpPendenteException("ACM_CONTRACT_MAPPING");
        if (versao.Snapshot.Centro == "3011" && versao.Snapshot.TipoContrato == "AC3P")
        {
            if (vinculos is null)
                throw new CapabilityMvpPendenteException("ACM_H4_GUARDIAN_STORE_UNAVAILABLE");
            var campos = MapeadorContratoAcmGuardian.MapearCamposConfirmados(versao.Snapshot);
            if (campos.NumeroContratoSap != versao.NumeroContrato)
                throw new CapabilityMvpPendenteException("ACM_H4_IDENTITY_MISMATCH");
            var link = await vinculos.ReservarAsync(versao, campos.NumeroReferencia, comando.Codigo, ct);
            if (link.IntegracaoIdOrigem != versao.IntegracaoId || link.VersaoH4 != versao.Versao ||
                link.NumeroContratoSap != versao.NumeroContrato ||
                link.NumeroReferenciaSolicitado != campos.NumeroReferencia ||
                link.CodigoContratoSolicitado != comando.Codigo)
                throw new CapabilityMvpPendenteException("ACM_H4_GUARDIAN_DURABLE_LINK_CONFLICT");
        }
        if (guardian is not IGuardianContratoSincronizacaoGateway sincronizacao)
            throw new CapabilityMvpPendenteException("ACM_CONTRACT_SYNC_UNAVAILABLE");
        var retorno = await new SincronizarContratoGuardian(sincronizacao)
            .ExecutarAsync(comando, ct, efeitos.MarcarEnvioIniciado);
        if (retorno.Acao == AcaoSincronizacaoContrato.Indeterminado)
            return new(StatusFluxoMvp.ResultadoIndeterminado, claim.CorrelationId, Operacao,
                Erro: "ACM_CONTRACT_SYNC_INDETERMINATE");
        if (versao.Snapshot.Centro == "3011" && versao.Snapshot.TipoContrato == "AC3P")
        {
            await vinculos!.RegistrarCodigoConfirmadoAsync(versao.IntegracaoId, retorno.CodigoConfirmado!, ct);
            // Reconciliation and durable identity are preserved before resolving scheduling data.
            var transportadora = await (transportadoras ?? new ResolverTransportadoraAcmPendente())
                .ResolverAsync(versao, ct);
            if (transportadora is null || string.IsNullOrWhiteSpace(transportadora.Codigo) ||
                string.IsNullOrWhiteSpace(transportadora.FonteTransacional))
                return new(StatusFluxoMvp.AguardandoDependenciaExterna, claim.CorrelationId, Operacao,
                    ResultadoFuncional: "BLOCKED_DATA", Erro: "ACM_CARRIER_NOT_RESOLVED",
                    ErroCategoria: "DADOS", ErroReprocessavel: false);
            // A proven carrier unlocks the next gate; scheduling requires its own confirmed dispatch.
            return new(StatusFluxoMvp.AguardandoDependenciaExterna, claim.CorrelationId, Operacao,
                ResultadoFuncional: "ACM_CARRIER_RESOLVED", Erro: "ACM_PRECADASTRO_DISPATCH_PENDING",
                ErroCategoria: "DEPENDENCIA", ErroReprocessavel: false);
        }
        return new(StatusFluxoMvp.Concluido, claim.CorrelationId, Operacao, ResultadoFuncional: comando.Codigo);
    }
}

public sealed class ProcessarPesagemAcm(
    IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados, IDecisoesFluxoMvp decisoes, TimeProvider tempo,
    IRepositorioCorrelacaoRecebimento correlacoes, IMapeadorPesagemAcmSap mapper, IReceberPesagemSap sap)
    : FluxoMvpCasoDeUso(FluxoMvp.AcmInPesagem, motor, resultados, decisoes, tempo)
{
    protected override async Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
    {
        var correlacao = await correlacoes.ObterPorDocumentoAsync(claim.ChaveNegocio, ct);
        if (correlacao?.Pesos is null || correlacao.DocumentoSap != claim.ChaveNegocio || string.IsNullOrWhiteSpace(correlacao.TicketGuardian))
            throw new CapabilityMvpPendenteException("ACM_EVENT5_MISSING");
        // H7 nao comprova a identidade do callback associada a versao H4 do ACM 3011.
        // Este gate nao transforma ID do callback em ID de pre-cadastro nem infere NumeroContrato por padding.
        if (correlacao.Entrada.Centro == "3011" && correlacao.Entrada.TipoContrato == "AC3P")
            throw new CapabilityMvpPendenteException("ACM_CALLBACK_H4_IDENTITY_NOT_PROVEN");
        var comando = mapper.Mapear(new Dictionary<string, object?>
        {
            ["DocumentoSap"] = correlacao.DocumentoSap,
            ["TicketGuardian"] = correlacao.TicketGuardian,
            ["PesoBruto"] = correlacao.Pesos.PesoBruto,
            ["Tara"] = correlacao.Pesos.Tara,
            ["PesoLiquido"] = correlacao.Pesos.PesoLiquido,
            ["NumeroContrato"] = correlacao.Entrada.Contrato,
            ["ContratoAcmSap"] = correlacao.Entrada.ContratoAcmSap
        });
        if (string.IsNullOrWhiteSpace(comando.ResourcePath) || comando.Payload.Count == 0)
            throw new CapabilityMvpPendenteException("ACM_WEIGHING_MAPPING");
        var resultado = await efeitos.ExecutarAsync(() => sap.EnviarAsync(comando, ct));
        if (resultado.Status != StatusEscritaSap.Sucesso) throw new InvalidOperationException("ACM_WEIGHING_NOT_CONFIRMED");
        return new(StatusFluxoMvp.Concluido, claim.CorrelationId, Operacao, ResultadoFuncional: resultado.Dados,
            PurchaseOrder: correlacao.Entrada.PedidoCompras);
    }
}

public sealed class ProcessarAcmSaida(
    IMotorIntegracaoRepository motor, IResultadoFluxoMvpStore resultados, IDecisoesFluxoMvp decisoes, TimeProvider tempo)
    : FluxoMvpCasoDeUso(FluxoMvp.AcmOut, motor, resultados, decisoes, tempo)
{
    protected override Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
        => throw new CapabilityMvpPendenteException("API7");
}
