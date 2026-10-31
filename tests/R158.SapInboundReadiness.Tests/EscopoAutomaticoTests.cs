using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;

namespace R158.SapInboundReadiness.Tests;

// R170: recorte continuo do cadastro ACM. A janela de admissao nao serve aqui porque so admite
// contratos que chegam com ela aberta, e o receptor so carimba a correlacao da janela quando roda
// em LoopbackBasicHttps - o que exigiria o SAP entregar por HTTPS. Estes testes fixam o que o
// recorte continuo aceita e, sobretudo, que identificador sem mapeamento NAO vira envio.
public sealed class EscopoAutomaticoTests
{
    private const string Parceiro = "0020000051";
    private const string Material = "000000000007000002";

    private static AcmAutomaticoOptions Escopo(params string[] excluidos) => new()
    {
        Enabled = true,
        AuthorizationReference = "R170-CONTINUO",
        Centro = "3011",
        Item = null,
        ExcludedContractNumbers = excluidos
    };

    private static ContratoSapToledoSnapshot Snapshot(string numero = "0000002001",
        string tipo = "AC3S", string pesagem = "S", string operacao = "40",
        string parceiro = Parceiro, string material = Material)
        => new(SistemaBalanca: "Toledo", NumeroContrato: numero, TipoContrato: tipo,
            DescricaoTipoContrato: "ACM contrato", DataEmissao: "2026-10-29", DataEntregaInicio: "2026-10-29",
            DataEntregaFim: "2026-12-31", DiasToler: 0, DataLimiteEntrega: null, StatusContrato: "T",
            QuantidadeContrato: 100000.000m, QuantidadeSaldoDevolucao: 0m, IdParceiro: parceiro,
            Material: material, TipoPesagem: pesagem, TabelaQualidadeID: "0000000078",
            TabelaQualidadeVersao: "001", TabelaQualidadeVersaoValidade: "001", Observacao: "",
            ValorUnitario: 1.00m, Centro: "3011", OperacaoContrato: operacao, CentroOpcional: []);

    // O gate decide por escopo e mapeamento; a integridade do fingerprint e verificada antes, em
    // MotorIntegracaoServico.DentroDoEscopoAsync. Aqui um valor sintetico basta.
    private static VersaoContratoSapToledo Versao(ContratoSapToledoSnapshot s, long versao = 1)
        => new(ResultadoRegistroVersaoContrato.Criada, s.NumeroContrato!, versao,
            new string('A', 64), Guid.NewGuid(), s);

    // O escopo continuo usa a MESMA regra de negocio da janela: os dois pares fechados, e nada mais.
    [Theory]
    [InlineData("AC3P", "E", "10", true)]
    [InlineData("AC3S", "S", "40", true)]
    // Operacao 30 e compra como a 10: o SAP usa mais de um codigo para o mesmo sentido, e exigir
    // um valor fixo travava contrato legitimo (foi o caso do 0000001165).
    [InlineData("AC3P", "E", "30", true)]
    [InlineData("AC3S", "S", "99", true)]
    [InlineData("AC3P", "S", "10", false)]
    [InlineData("AC3S", "E", "40", false)]
    [InlineData("AC3X", "E", "10", false)]
    public void Aceita_os_dois_pares_de_negocio_e_recusa_misturas(string tipo, string pesagem, string operacao, bool esperado)
        => Assert.Equal(esperado, Escopo().SnapshotPermitido(Snapshot(tipo: tipo, pesagem: pesagem, operacao: operacao)));

    // Sem lista de parceiros ou materiais permitidos: quem barra identificador desconhecido e o
    // mapeamento, nao o escopo. Isso mantem o contrato visivel no FugaTES em vez de recusa-lo.
    [Fact]
    public void Escopo_nao_filtra_por_parceiro_ou_material()
        => Assert.True(Escopo().SnapshotPermitido(Snapshot(parceiro: "0020000184", material: "000000000007000000")));

    [Fact]
    public void Contratos_historicos_continuam_bloqueados()
    {
        foreach (var historico in new[] { "1031", "1140", "1155" })
            Assert.False(Escopo().SnapshotPermitido(Snapshot(numero: historico)));
    }

    [Fact]
    public void Contrato_excluido_explicitamente_e_bloqueado()
        => Assert.False(Escopo("2001").SnapshotPermitido(Snapshot(numero: "0000002001")));

    [Theory]
    [InlineData(null)]
    [InlineData("PENDENTE")]
    [InlineData(" ")]
    public void Sem_referencia_de_autorizacao_comprovada_o_escopo_e_invalido(string? referencia)
    {
        var escopo = Escopo();
        escopo.AuthorizationReference = referencia;
        Assert.False(escopo.EhValida());
        Assert.False(escopo.SnapshotPermitido(Snapshot()));
    }

    [Fact]
    public void Centro_fora_do_aprovado_invalida_o_escopo()
    {
        var escopo = Escopo();
        escopo.Centro = "3020";
        Assert.False(escopo.EhValida());
    }

    // Os tres recortes sao exclusivos: combinados, nao se sabe qual regra decide o despacho.
    [Fact]
    public void Recortes_combinados_invalidam_a_configuracao_do_motor()
    {
        static MotorIntegracaoOptions Base() => new()
        {
            LeaseDuration = TimeSpan.FromMinutes(3),
            LeaseRenewInterval = TimeSpan.FromSeconds(20),
            RetryBackoff = TimeSpan.FromDays(1)
        };

        var soAutomatico = Base();
        soAutomatico.AcmAutomatico = Escopo();
        Assert.True(soAutomatico.EhValida());

        var comPiloto = Base();
        comPiloto.AcmAutomatico = Escopo();
        comPiloto.AcmPilotNumeroContrato = "0000001160";
        comPiloto.AcmPilotCentro = "3011";
        Assert.False(comPiloto.EhValida());

        var comJanela = Base();
        comJanela.AcmAutomatico = Escopo();
        comJanela.AcmInboundWindow = new AcmInboundWindowOptions { Enabled = true };
        Assert.False(comJanela.EhValida());
    }

    private static AcmR57NormalOptions Opcoes(
        AcmR57ApprovedScopePartnerMapping[]? parceiros = null,
        AcmR57ApprovedScopeMaterialMapping[]? materiais = null) => new()
    {
        Enabled = true,
        ExecutionAuthorized = true,
        AuthorizationReference = "R170-CONTINUO",
        GuardianEnvironment = "HML",
        GuardianIdentityEvidence = "R170-GUARDIAN",
        PostgreSqlIdentityEvidence = "R170-PG",
        JournalEvidence = "R170-JOURNAL",
        IdentifierMappingEvidence = "R170-MAPEAMENTO",
        AcmAutomatico = Escopo(),
        ApprovedPartnerMappings = parceiros ??
            [new() { Centro = "3011", SapPartnerId = Parceiro, GuardianCode = "20000051", EvidenceReference = "R170-MAPEAMENTO" }],
        ApprovedMaterialMappings = materiais ??
            [new() { Centro = "3011", SapMaterial = Material, GuardianCode = "7000000", EvidenceReference = "R170-MAPEAMENTO" }]
    };

    [Fact]
    public void Com_mapeamento_aprovado_o_gate_libera()
        => Assert.Null(new AcmR57NormalRuntimeGate(Opcoes()).Bloqueio(Versao(Snapshot())));

    // O caso central: parceiro sem codigo Guardian aprovado NAO vira envio.
    [Fact]
    public void Parceiro_sem_mapeamento_bloqueia_antes_do_envio()
        => Assert.Equal("R170_PARTNER_MAPPING_REQUIRED",
            new AcmR57NormalRuntimeGate(Opcoes()).Bloqueio(Versao(Snapshot(parceiro: "0020000184"))));

    [Fact]
    public void Material_sem_mapeamento_bloqueia_antes_do_envio()
        => Assert.Equal("R170_MATERIAL_MAPPING_REQUIRED",
            new AcmR57NormalRuntimeGate(Opcoes()).Bloqueio(Versao(Snapshot(material: "000000000007000000"))));

    // Duas entradas para o mesmo identificador sao ambiguidade, nao "a primeira vale".
    [Fact]
    public void Mapeamento_duplicado_bloqueia_por_ambiguidade()
    {
        var duplicado = Opcoes(parceiros:
        [
            new() { Centro = "3011", SapPartnerId = Parceiro, GuardianCode = "20000051", EvidenceReference = "R170-MAPEAMENTO" },
            new() { Centro = "3011", SapPartnerId = Parceiro, GuardianCode = "10000", EvidenceReference = "R170-MAPEAMENTO" }
        ]);
        Assert.Equal("R170_PARTNER_MAPPING_REQUIRED", new AcmR57NormalRuntimeGate(duplicado).Bloqueio(Versao(Snapshot())));
    }

    [Fact]
    public void Mapeamento_sem_evidencia_nao_conta_como_aprovado()
    {
        var semEvidencia = Opcoes(parceiros:
            [new() { Centro = "3011", SapPartnerId = Parceiro, GuardianCode = "20000051", EvidenceReference = "PENDENTE" }]);
        Assert.Equal("R170_PARTNER_MAPPING_REQUIRED", new AcmR57NormalRuntimeGate(semEvidencia).Bloqueio(Versao(Snapshot())));
    }

    // Versao diferente de 1 nao e contrato novo; o recorte continuo so cadastra contrato novo.
    [Fact]
    public void Versao_diferente_de_um_e_recusada()
        => Assert.Equal("R170_SCOPE_INVALID", new AcmR57NormalRuntimeGate(Opcoes()).Bloqueio(Versao(Snapshot(), versao: 2)));

    // No recorte continuo a correlacao vem do receptor comum, sem prefixo de janela. Exigir o
    // prefixo aqui bloquearia tudo; exigi-lo no modo janela continua valendo.
    [Fact]
    public void Correlacao_sem_prefixo_e_aceita_no_recorte_continuo()
    {
        var gate = new AcmR57NormalRuntimeGate(Opcoes());
        Assert.True(gate.CorrelationPermitida(Guid.NewGuid().ToString("N")));
        Assert.True(gate.ExigeContratoNovo);
    }

    [Fact]
    public void No_modo_janela_a_correlacao_sem_prefixo_continua_recusada()
    {
        var janela = new AcmR57NormalRuntimeGate(new AcmR57NormalOptions
        {
            AcmInboundWindow = new AcmInboundWindowOptions
            {
                Enabled = true,
                WindowId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
                AuthorizationReference = "R170-JANELA",
                NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                UntilUtc = DateTimeOffset.UtcNow.AddMinutes(10),
                Centro = "3011",
                AllowedSapPartners = [Parceiro],
                AllowedSapMaterials = [Material]
            }
        });
        Assert.False(janela.CorrelationPermitida(Guid.NewGuid().ToString("N")));
    }
}
