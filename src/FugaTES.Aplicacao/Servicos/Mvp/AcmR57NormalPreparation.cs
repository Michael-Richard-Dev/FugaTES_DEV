using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Configuracao;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed record AcmR57ApprovedPartnerMapping
{
    public string? NumeroContrato { get; init; }
    public string? Item { get; init; }
    public string? Centro { get; init; }
    public string? SapPartnerId { get; init; }
    public string? GuardianCode { get; init; }
    public string? EvidenceReference { get; init; }
}

public sealed record AcmR57ApprovedMaterialMapping
{
    public string? NumeroContrato { get; init; }
    public string? Item { get; init; }
    public string? Centro { get; init; }
    public string? SapMaterial { get; init; }
    public string? GuardianCode { get; init; }
    public string? EvidenceReference { get; init; }
}

// R170: mapeamento do recorte continuo. Chaveado por centro + identificador SAP, NAO por numero de
// contrato - e isso que permite atender contratos novos sem reconfigurar nada a cada chegada. Sem
// WindowId, porque nao ha janela. Um identificador sem entrada aqui bloqueia o envio: fail-closed.
public sealed record AcmR57ApprovedScopePartnerMapping
{
    public string? Centro { get; init; }
    public string? Item { get; init; }
    public string? SapPartnerId { get; init; }
    public string? GuardianCode { get; init; }
    public string? EvidenceReference { get; init; }
}

public sealed record AcmR57ApprovedScopeMaterialMapping
{
    public string? Centro { get; init; }
    public string? Item { get; init; }
    public string? SapMaterial { get; init; }
    public string? GuardianCode { get; init; }
    public string? EvidenceReference { get; init; }
}

// Set only by the dedicated command host after validating the server-owned unit approval.
// It never expands the automatic/window selectors or the global historical cutoff.
public sealed record AcmR57ManualRecoveryScope
{
    public bool SoapOnly{get;init;}
    public Guid IntegrationId{get;init;}
    public string Contract{get;init;}="";
    public string? Item{get;init;}
    public long Version{get;init;}
    public string Fingerprint{get;init;}="";
    public DateTimeOffset ReceivedUtc{get;init;}
    public string AuthorizationReference{get;init;}="";
    public DateTimeOffset NotBeforeUtc{get;init;}
    public DateTimeOffset UntilUtc{get;init;}
    public bool Matches(VersaoContratoSapToledo v)=>IntegrationId!=Guid.Empty&&IntegrationId==v.IntegracaoId&&
        Contract==v.NumeroContrato&&Contract==v.Snapshot.NumeroContrato&&Contract.Length>0&&Contract.All(char.IsAsciiDigit)&&
        Item==v.Snapshot.Item&&(Item is null||AcmR57NormalPreparation.Proven(Item))&&Version>0&&Version==v.Versao&&
        Fingerprint.Length==64&&Fingerprint.All(char.IsAsciiHexDigit)&&Fingerprint==v.FingerprintSha256&&
        ReceivedUtc!=default&&ReceivedUtc.Offset==TimeSpan.Zero&&v.RecebidoEmUtc==ReceivedUtc&&
        AcmR57NormalPreparation.Proven(AuthorizationReference)&&NotBeforeUtc.Offset==TimeSpan.Zero&&
        UntilUtc.Offset==TimeSpan.Zero&&UntilUtc>NotBeforeUtc;
    public bool IsOpen(DateTimeOffset now)=>now>=NotBeforeUtc&&now<UntilUtc;
}

public sealed record AcmR57NormalOptions
{
    public AcmR57ManualRecoveryScope? ManualRecoveryScope {get;init;}
    public bool DynamicMaterialValidationEnabled { get; init; }
    public DateTimeOffset? DynamicMaterialValidationFromUtc { get; init; }
    public bool DynamicEmitterValidationEnabled { get; init; }
    public int DynamicEmitterSignatureId { get; init; }
    public DateTimeOffset? DynamicEmitterValidationFromUtc { get; init; }
    public AcmInboundWindowOptions AcmInboundWindow { get; init; } = new();
    public AcmAutomaticoOptions AcmAutomatico { get; init; } = new();
    public AcmR57ApprovedScopePartnerMapping[] ApprovedPartnerMappings { get; init; } = [];
    public AcmR57ApprovedScopeMaterialMapping[] ApprovedMaterialMappings { get; init; } = [];
    public AcmR57ApprovedWindowPartnerMapping[] ApprovedWindowPartnerMappings { get; init; } = [];
    public AcmR57ApprovedWindowMaterialMapping[] ApprovedWindowMaterialMappings { get; init; } = [];
    public bool Enabled { get; init; }
    public bool ExecutionAuthorized { get; init; }
    public string? AuthorizationReference { get; init; }
    public string? GuardianEnvironment { get; init; }
    public string? GuardianIdentityEvidence { get; init; }
    public string? PostgreSqlIdentityEvidence { get; init; }
    public string? JournalEvidence { get; init; }
    public bool SapIdentifiersCopiedVerbatimApproved { get; init; }
    public bool SapContractAndMaterialCopiedVerbatimApproved { get; init; }
    public bool SapContractCopiedVerbatimApproved { get; init; }
    public AcmR57ApprovedPartnerMapping? ApprovedPartnerMapping { get; init; }
    public AcmR57ApprovedMaterialMapping? ApprovedMaterialMapping { get; init; }
    public string? IdentifierMappingEvidence { get; init; }
    public decimal? ValorProximidadeFimContrato { get; init; }
    public string? ProximityValueEvidence { get; init; }
    public string? CallbackUrl { get; init; }
    public bool CallbackApproved { get; init; }
    public string? CallbackEvidence { get; init; }
    public AcmR104CallbackBinding? CallbackBinding { get; init; }
    public int IdAgenda { get; init; }
}

public sealed record AcmR57ApprovedWindowPartnerMapping
{
    public string? WindowId { get; init; }
    public string? Centro { get; init; }
    public string? Item { get; init; }
    public string? SapPartnerId { get; init; }
    public string? GuardianCode { get; init; }
    public string? EvidenceReference { get; init; }
}

public sealed record AcmR57ApprovedWindowMaterialMapping
{
    public string? WindowId { get; init; }
    public string? Centro { get; init; }
    public string? Item { get; init; }
    public string? SapMaterial { get; init; }
    public string? GuardianCode { get; init; }
    public string? EvidenceReference { get; init; }
}

public sealed class AcmR57NormalRuntimeGate(AcmR57NormalOptions options, TimeProvider? timeProvider, AcmGuardianEmitterResolver? emitterResolver,AcmGuardianMaterialResolver? materialResolver) : IAcmR57RuntimeGate
{
    public AcmR57NormalRuntimeGate(AcmR57NormalOptions options,TimeProvider? timeProvider,AcmGuardianEmitterResolver? emitterResolver):this(options,timeProvider,emitterResolver,null){}
    public AcmR57NormalRuntimeGate(AcmR57NormalOptions options,TimeProvider? timeProvider=null):this(options,timeProvider,null,null){}
    private bool Janela => options.AcmInboundWindow?.Enabled == true;
    private bool Automatico => options.AcmAutomatico?.Enabled == true;
    // Os dois recortes exigem contrato NOVO no Guardian: um cadastro preexistente obriga
    // reconciliacao explicita no pipeline, nunca adocao silenciosa.
    public bool ExigeContratoNovo => Janela || Automatico || options.ManualRecoveryScope is not null;
    // O prefixo de correlacao e carimbado pelo receptor e so existe no modo janela. No recorte
    // continuo o contrato chega pelo receptor comum, com correlacao sem prefixo.
    public bool CorrelationPermitida(string correlationId) => !Janela || options.AcmInboundWindow.CorrelationConfere(correlationId);
    public string? Bloqueio(VersaoContratoSapToledo version)
    {
        if(options.ManualRecoveryScope is {} manual && (Janela||Automatico||!options.DynamicEmitterValidationEnabled||
            options.DynamicEmitterSignatureId<=0||!manual.Matches(version)||!manual.IsOpen((timeProvider??TimeProvider.System).GetUtcNow())))
            return "ACM_PENDING_MANUAL_SCOPE_REJECTED";
        if (Janela && !options.AcmInboundWindow.EstaAberta((timeProvider ?? TimeProvider.System).GetUtcNow()))
            return "R165AG_WINDOW_CLOSED";
        if (Janela && (version.Versao != 1 || !options.AcmInboundWindow.SnapshotPermitido(version.Snapshot)))
            return "R165AG_WINDOW_SCOPE_INVALID";
        if (Automatico && (version.Versao != 1 || !options.AcmAutomatico.SnapshotPermitido(version.Snapshot)))
            return "R170_SCOPE_INVALID";
        // O recorte do motor ja nao varre contrato de antes do corte. Este gate repete a regra
        // porque ele tambem serve caminhos que nao passam pelo recorte - e porque uma regra de
        // escopo conferida num lugar so e uma regra que a proxima composicao esquece.
        if (Automatico && !options.AcmAutomatico.RecebimentoNoCorte(version.RecebidoEmUtc))
            return "R173_ANTERIOR_AO_CORTE_AUTOMATICO";
        if (!options.Enabled || !options.ExecutionAuthorized || !AcmR57NormalPreparation.Proven(options.AuthorizationReference))
            return "R165U_EXECUTION_DISABLED";
        if (options.GuardianEnvironment != "HML" || !AcmR57NormalPreparation.Proven(options.GuardianIdentityEvidence))
            return "R165U_GUARDIAN_IDENTITY_REQUIRED";
        if (!AcmR57NormalPreparation.Proven(options.PostgreSqlIdentityEvidence) || !AcmR57NormalPreparation.Proven(options.JournalEvidence))
            return "R165U_DURABLE_READINESS_REQUIRED";
        if (!CentrosAcm.EhPermitido(version.Snapshot.Centro) ||
            !AcmInboundWindowOptions.EscopoContratoAprovado(version.Snapshot.TipoContrato, version.Snapshot.TipoPesagem))
            return "R165U_BUSINESS_SCOPE_NOT_APPROVED";
        try { _ = AcmR57NormalPreparation.ResolverCodigoEmissor(options, version, emitterResolver,materialResolver); return null; }
        catch (AcmR57BlockedException ex) { return ex.Code; }
    }
}

public sealed record AcmR57NormalContract(ContratoGuardianSet26 Contrato, string NumeroReferencia);

public interface IAcmR57NormalPreparacao : IAcmR57Preparacao
{
    // Default preserves every existing preparation; the dedicated command scope is explicit.
    bool SomenteCadastroSoap(VersaoContratoSapToledo version) => false;
    AcmR57NormalContract PrepararContrato(VersaoContratoSapToledo version);
    PreCadastroAcmR57 PrepararProtocolo(VersaoContratoSapToledo version, AcmR57NormalContract contract);
}

public sealed class AcmR57NormalPreparation(AcmR57NormalOptions options, AcmGuardianEmitterResolver? emitterResolver,AcmGuardianMaterialResolver? materialResolver) : IAcmR57NormalPreparacao, IAcmR57DynamicEmitterPreparation, IAcmR57DynamicMaterialPreparation
{
    public AcmR57NormalPreparation(AcmR57NormalOptions options,AcmGuardianEmitterResolver? emitterResolver):this(options,emitterResolver,null){}
    public AcmR57NormalPreparation(AcmR57NormalOptions options):this(options,null,null){}
    public Task ValidarMaterialAsync(VersaoContratoSapToledo version,CancellationToken ct)
        =>materialResolver?.ValidarAsync(version,ct)??Task.FromException(new AcmR57BlockedException("ACM_MATERIAL_RESOLVER_UNAVAILABLE"));
    public bool SomenteCadastroSoap(VersaoContratoSapToledo version)
        => options.ManualRecoveryScope is {SoapOnly:true} manual && manual.Matches(version);
    public Task ValidarEmissorAsync(VersaoContratoSapToledo version,CancellationToken ct)
        => emitterResolver?.ValidarAsync(version,ct) ?? Task.FromException(new AcmR57BlockedException("R171_EMITTER_RESOLVER_UNAVAILABLE"));

    public AcmR57Vinculos Obter(VersaoContratoSapToledo version)
        => throw new AcmR57BlockedException("R165U_PER_STAGE_PREPARATION_REQUIRED");

    internal static bool Proven(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
        value.Length <= 160 && !value.Any(char.IsControl) &&
        !new[] { "PENDENTE", "NOT_APPROVED", "NAO VALIDADO", "NÃO VALIDADO" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static string Required(string? value, string code, int max = 160)
        => Proven(value) && value!.Length <= max ? value : throw new AcmR57BlockedException(code);

    internal static string ResolverCodigoEmissor(AcmR57NormalOptions options, VersaoContratoSapToledo version, AcmGuardianEmitterResolver? emitterResolver = null,AcmGuardianMaterialResolver? materialResolver=null)
    {
        if(AcmGuardianEmitterResolver.UsesDynamic(options,version))
        {
            if(AcmGuardianMaterialResolver.UsesDynamic(options,version))
                _=materialResolver?.Exigir(version)??throw new AcmR57BlockedException("ACM_MATERIAL_VALIDATION_REQUIRED");
            else _=ResolverMaterialEscopo(options,version);
            return emitterResolver?.Exigir(version) ?? throw new AcmR57BlockedException("R171_EMITTER_VALIDATION_REQUIRED");
        }
        if (options.AcmInboundWindow?.Enabled == true) return ResolverWindowMappings(options, version).Partner;
        if (UsaMapeamentoPorEscopo(options)) return ResolverScopeMappings(options, version).Partner;
        if (!Proven(options.IdentifierMappingEvidence))
            throw new AcmR57BlockedException("R165U_IDENTIFIER_MAPPING_REQUIRED");
        var mapping = options.ApprovedPartnerMapping;
        var s = version.Snapshot;
        var mappedMaterialMode = options.SapContractCopiedVerbatimApproved;
        if ((mappedMaterialMode || options.ApprovedMaterialMapping is not null) &&
            (!mappedMaterialMode || options.ApprovedMaterialMapping is null ||
             options.SapContractAndMaterialCopiedVerbatimApproved || options.SapIdentifiersCopiedVerbatimApproved))
            throw new AcmR57BlockedException("R165Y_MATERIAL_MAPPING_APPROVAL_REQUIRED");
        var contractMappingApproved = options.SapContractAndMaterialCopiedVerbatimApproved || mappedMaterialMode;
        // Legacy approval covers all identifiers. An explicit partner mapping is a separate, exclusive mode.
        if (mapping is null && !contractMappingApproved)
        {
            if (!options.SapIdentifiersCopiedVerbatimApproved)
                throw new AcmR57BlockedException("R165U_IDENTIFIER_MAPPING_REQUIRED");
            return Required(s.IdParceiro, "R165U_PARTNER_REQUIRED", 20);
        }
        if (!contractMappingApproved || options.SapIdentifiersCopiedVerbatimApproved ||
            mapping is null || new[] { mapping.NumeroContrato, mapping.Centro,
                mapping.SapPartnerId, mapping.GuardianCode, mapping.EvidenceReference }.Any(value => !Proven(value)) ||
            (mapping.Item is not null && !Proven(mapping.Item)) ||
            mapping.NumeroContrato != version.NumeroContrato || mapping.NumeroContrato != s.NumeroContrato ||
            mapping.Item != s.Item || mapping.Centro != s.Centro || mapping.SapPartnerId != s.IdParceiro ||
            mapping.SapPartnerId!.Length > 20 || mapping.GuardianCode!.Length > 20)
            throw new AcmR57BlockedException("R165U_PARTNER_MAPPING_REQUIRED");
        return mapping.GuardianCode!;
    }

    private static string ResolverCodigoItem(AcmR57NormalOptions options, VersaoContratoSapToledo version,AcmGuardianMaterialResolver? materialResolver=null)
    {
        if(AcmGuardianMaterialResolver.UsesDynamic(options,version))return materialResolver?.Exigir(version)??throw new AcmR57BlockedException("ACM_MATERIAL_VALIDATION_REQUIRED");
        if(AcmGuardianEmitterResolver.UsesDynamic(options,version))return ResolverMaterialEscopo(options,version);
        if (options.AcmInboundWindow?.Enabled == true) return ResolverWindowMappings(options, version).Material;
        if (UsaMapeamentoPorEscopo(options)) return ResolverScopeMappings(options, version).Material;
        if (!options.SapContractCopiedVerbatimApproved && options.ApprovedMaterialMapping is null)
            return Required(version.Snapshot.Material, "R165U_MATERIAL_REQUIRED", 30);
        var mapping = options.ApprovedMaterialMapping;
        var s = version.Snapshot;
        if (!options.SapContractCopiedVerbatimApproved || options.SapContractAndMaterialCopiedVerbatimApproved ||
            options.SapIdentifiersCopiedVerbatimApproved || mapping is null ||
            new[] { mapping.NumeroContrato, mapping.Centro, mapping.SapMaterial, mapping.GuardianCode,
                mapping.EvidenceReference }.Any(value => !Proven(value)) ||
            (mapping.Item is not null && !Proven(mapping.Item)) || mapping.NumeroContrato != version.NumeroContrato ||
            mapping.NumeroContrato != s.NumeroContrato || mapping.Item != s.Item || mapping.Centro != s.Centro ||
            mapping.SapMaterial != s.Material || mapping.GuardianCode!.Length > 30)
            throw new AcmR57BlockedException("R165Y_MATERIAL_MAPPING_REQUIRED");
        return mapping.GuardianCode;
    }

    // R170: resolucao do recorte continuo. Mesma forma do par de janela, chaveada por centro +
    // identificador SAP. Exige exatamente UMA entrada por identificador: zero bloqueia por falta de
    // aprovacao, duas bloqueiam por ambiguidade. Em nenhum dos casos algo e enviado ao Guardian.
    // A tabela de mapeamentos e o que define o modo, nao o recorte de selecao: assim o motor
    // continuo e o reenvio manual por piloto resolvem identificadores pela MESMA fonte.
    internal static string ResolverMaterialEscopo(AcmR57NormalOptions options,VersaoContratoSapToledo version)
    {
        var s=version.Snapshot;
        if(version.Versao!=1&&options.ManualRecoveryScope?.Matches(version)!=true||!CentrosAcm.EhPermitido(s.Centro)||!AcmInboundWindowOptions.EscopoContratoAprovado(s.TipoContrato,s.TipoPesagem))throw new AcmR57BlockedException("R170_SCOPE_INVALID");
        var materials=options.ApprovedMaterialMappings.Where(m=>m.Centro==s.Centro&&m.Item==s.Item&&m.SapMaterial==s.Material&&Proven(m.GuardianCode)&&m.GuardianCode!.Length<=30&&Proven(m.EvidenceReference)).ToArray();
        if(materials.Length!=1)throw new AcmR57BlockedException("R170_MATERIAL_MAPPING_REQUIRED");
        return materials[0].GuardianCode!;
    }

    private static bool UsaMapeamentoPorEscopo(AcmR57NormalOptions o) =>
        o.ApprovedPartnerMappings.Length > 0 || o.ApprovedMaterialMappings.Length > 0;

    private static (string Partner, string Material) ResolverScopeMappings(AcmR57NormalOptions options, VersaoContratoSapToledo version)
    {
        var s = version.Snapshot;
        // Deliberadamente NAO depende de AcmAutomatico: estes mapeamentos descrevem identificadores
        // aprovados para o centro, nao um modo de selecao de contratos. Assim o reenvio manual, que
        // roda ligado a um unico contrato pelo piloto, usa a mesma tabela de codigos do motor
        // continuo, em vez de exigir um mapeamento por contrato que ninguem manteria.
        if (version.Versao != 1 || !CentrosAcm.EhPermitido(s.Centro) ||
            !AcmInboundWindowOptions.EscopoContratoAprovado(s.TipoContrato, s.TipoPesagem))
            throw new AcmR57BlockedException("R170_SCOPE_INVALID");
        bool Common(string? centre, string? item, string? code, string? evidence, int max) =>
            centre == s.Centro && CentrosAcm.EhPermitido(centre) && item == s.Item &&
            Proven(code) && code!.Length <= max && Proven(evidence);
        var partners = options.ApprovedPartnerMappings
            .Where(m => Common(m.Centro, m.Item, m.GuardianCode, m.EvidenceReference, 20) && m.SapPartnerId == s.IdParceiro)
            .ToArray();
        if (partners.Length != 1) throw new AcmR57BlockedException("R170_PARTNER_MAPPING_REQUIRED");
        var materials = options.ApprovedMaterialMappings
            .Where(m => Common(m.Centro, m.Item, m.GuardianCode, m.EvidenceReference, 30) && m.SapMaterial == s.Material)
            .ToArray();
        if (materials.Length != 1) throw new AcmR57BlockedException("R170_MATERIAL_MAPPING_REQUIRED");
        return (partners[0].GuardianCode!, materials[0].GuardianCode!);
    }

    private static (string Partner, string Material) ResolverWindowMappings(AcmR57NormalOptions options, VersaoContratoSapToledo version)
    {
        var w = options.AcmInboundWindow; var s = version.Snapshot;
        bool Common(string? id, string? centre, string? item, string? code, string? evidence, int max) =>
            Guid.TryParseExact(id, "N", out var mappingId) && mappingId == Guid.ParseExact(w.WindowId!, "N") &&
            centre == w.Centro && centre == s.Centro && item == w.Item && item == s.Item &&
            Proven(code) && code!.Length <= max && Proven(evidence);
        if (!w.EhValida() || !w.SnapshotPermitido(s) || version.Versao != 1 ||
            !options.SapContractCopiedVerbatimApproved || options.SapIdentifiersCopiedVerbatimApproved ||
            options.SapContractAndMaterialCopiedVerbatimApproved || !Proven(options.IdentifierMappingEvidence) ||
            options.ApprovedPartnerMapping is not null || options.ApprovedMaterialMapping is not null ||
            options.ApprovedWindowPartnerMappings is null || options.ApprovedWindowMaterialMappings is null)
            throw new AcmR57BlockedException("R165AG_WINDOW_MAPPING_REQUIRED");
        var partners = options.ApprovedWindowPartnerMappings.Where(m => m is not null && m.SapPartnerId == s.IdParceiro &&
            Common(m.WindowId, m.Centro, m.Item, m.GuardianCode, m.EvidenceReference, 20)).ToArray();
        var materials = options.ApprovedWindowMaterialMappings.Where(m => m is not null && m.SapMaterial == s.Material &&
            Common(m.WindowId, m.Centro, m.Item, m.GuardianCode, m.EvidenceReference, 30)).ToArray();
        if (partners.Length != 1 || materials.Length != 1)
            throw new AcmR57BlockedException("R165AG_WINDOW_MAPPING_REQUIRED");
        return (partners[0].GuardianCode!, materials[0].GuardianCode!);
    }

    public AcmR57NormalContract PrepararContrato(VersaoContratoSapToledo version)
    {
        var s = version.Snapshot;
        // R170: o par de negocio e validado em conjunto; pesagem solta nao decide mais nada.
        if (!AcmInboundWindowOptions.EscopoContratoAprovado(s.TipoContrato, s.TipoPesagem))
            throw new AcmR57BlockedException("R165U_BUSINESS_SCOPE_NOT_APPROVED");
        CamposAcmGuardianConfirmados fields;
        try { fields = MapeadorContratoAcmGuardian.MapearCamposConfirmados(s); }
        catch (FugaTES.Aplicacao.Servicos.SAP.DecisaoFuncionalPendenteSapException)
        { throw new AcmR57BlockedException("R165U_BUSINESS_MAPPING_NOT_APPROVED"); }
        var codigoEmissor = ResolverCodigoEmissor(options, version, emitterResolver,materialResolver);
        if (options.ValorProximidadeFimContrato is null or < 0 || !Proven(options.ProximityValueEvidence))
            throw new AcmR57BlockedException("R165U_PROXIMITY_VALUE_APPROVAL_REQUIRED");
        if (s.QuantidadeContrato is null or < 0)
            throw new AcmR57BlockedException("R165U_QUANTITY_REQUIRED");
        var contract = new ContratoGuardianSet26(
            Required(s.NumeroContrato, "R165U_CONTRACT_ID_REQUIRED", 20),
            Required(s.DescricaoTipoContrato, "R165U_CONTRACT_DESCRIPTION_REQUIRED"),
            s.QuantidadeContrato.Value, fields.Estado, options.ValorProximidadeFimContrato.Value, fields.TipoLancamento)
        {
            CodigoEmissor = codigoEmissor,
            CodigoItem = ResolverCodigoItem(options, version,materialResolver)
        };
        // Do not silently replace the separately homologated R69 wire identity.
        // Its normalized code/emitter cannot be inferred from the padded SAP identifiers.
        if (s.NumeroContrato.TrimStart('0') == "1106" &&
            (contract.Codigo != "1106" || contract.CodigoEmissor != "20000184" || contract.CodigoItem != "7000000"))
            throw new AcmR57BlockedException("R165U_HISTORICAL_MAPPING_RECONCILIATION_REQUIRED");
        return new(contract, Required(fields.NumeroReferencia, "R165U_REFERENCE_INVALID", 10));
    }

    public PreCadastroAcmR57 PrepararProtocolo(VersaoContratoSapToledo version, AcmR57NormalContract contract)
    {
        if (options.IdAgenda is < 0 or > 1) throw new AcmR57BlockedException("R165Y_AGENDA_INVALID");
        if (!Proven(options.CallbackEvidence) ||
            !Uri.TryCreate(options.CallbackUrl, UriKind.Absolute, out var callback) ||
            !string.IsNullOrEmpty(callback.Query) || !string.IsNullOrEmpty(callback.Fragment) ||
            !AcmR104CallbackPolicy.Validar(options.CallbackUrl, options.CallbackApproved, options.CallbackBinding))
            throw new AcmR57BlockedException("R165U_CALLBACK_APPROVAL_REQUIRED");
        var s = version.Snapshot;
        var request = new PreCadastroAcmR57(options.CallbackUrl, new(0, contract.Contrato.CodigoItem!,
            contract.Contrato.CodigoEmissor!, contract.Contrato.Codigo, null, contract.NumeroReferencia,
            [new(1000, s.TipoContrato!), new(1002, s.Centro!), new(1003, s.OperacaoContrato!)]) { IdAgenda = options.IdAgenda })
        {
            CallbackHomologado = options.CallbackApproved, CallbackBinding = options.CallbackBinding,
            CriacaoInicialSemDadosPesagem = true
        };
        AcmR57Mapping.Validar(request);
        return request;
    }
}
