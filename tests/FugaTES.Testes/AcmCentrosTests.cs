using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Testes;

/// <summary>
/// Os dois centros do fluxo ACM. Decisao do operador em 04/11/2026: o cadastro de contrato no
/// Guardian atende 3011 E 3008.
///
/// Estes testes existem porque a falha anterior foi SILENCIOSA: os contratos 1207 e 1208, do centro
/// 3008, chegaram, viraram integracao e ficaram PENDENTE para sempre - o recorte do motor nao os
/// considerava trabalho seu e, por nao serem trabalho seu, nao geravam recusa nenhuma. Nada no
/// journal, nada no banco. Um teste que apenas conferisse "contrato de centro errado e recusado"
/// nao teria pegado isso; e preciso afirmar o caminho FELIZ dos dois centros.
/// </summary>
public sealed class AcmCentrosTests
{
    private static ContratoSapToledoSnapshot Snapshot(string centro, string tipo = "AC3P",
        string pesagem = "E") =>
        AcmGuardianReferenceMapperTests.Snapshot() with
        {
            // 1207 de proposito: e um dos contratos reais que ficaram presos, e NAO esta na lista
            // de contratos historicos que o recorte continuo exclui (1031, usado no helper, esta).
            NumeroContrato = "0000001207",
            Centro = centro, TipoContrato = tipo, TipoPesagem = pesagem,
            DescricaoTipoContrato = "Compra", QuantidadeContrato = 50m, IdParceiro = "20000051"
        };

    private static AcmAutomaticoOptions Escopo(params string[] centros) => new()
    {
        Enabled = true,
        AuthorizationReference = "R170-OPERADOR-CONTINUO",
        Centros = centros
    };

    [Theory]
    [InlineData("3011")]
    [InlineData("3008")]
    public void Centro_do_fluxo_e_aprovado(string centro)
        => Assert.True(CentrosAcm.EhPermitido(centro));

    [Theory]
    [InlineData("3020")]
    [InlineData("3012")]
    [InlineData("")]
    [InlineData(" 3011")]
    [InlineData("3011 ")]
    [InlineData(null)]
    public void Centro_de_fora_continua_reprovando(string? centro)
        => Assert.False(CentrosAcm.EhPermitido(centro));

    // O recorte continuo precisa aceitar os DOIS centros ao mesmo tempo: MotorIntegracaoOptions
    // recusa dois recortes ativos em paralelo, entao atender 3011 e 3008 tinha de caber dentro de um.
    [Theory]
    [InlineData("3011")]
    [InlineData("3008")]
    public void Recorte_continuo_com_os_dois_centros_aceita_cada_um(string centro)
    {
        var escopo = Escopo("3011", "3008");
        Assert.True(escopo.EhValida());
        Assert.True(escopo.SnapshotPermitido(Snapshot(centro)));
    }

    // A configuracao que ja esta na VM (um unico Centro=3011) continua valendo e continua
    // RESTRINGINDO: quem declarou um centro nao passa a receber o outro de brinde.
    [Fact]
    public void Centro_unico_configurado_nao_abre_o_outro_centro()
    {
        var escopo = new AcmAutomaticoOptions
        { Enabled = true, AuthorizationReference = "R170-OPERADOR-CONTINUO", Centro = "3011" };
        Assert.True(escopo.EhValida());
        Assert.True(escopo.SnapshotPermitido(Snapshot("3011")));
        Assert.False(escopo.SnapshotPermitido(Snapshot("3008")));
    }

    [Fact]
    public void Recorte_sem_centro_declarado_e_invalido()
    {
        var escopo = new AcmAutomaticoOptions
        { Enabled = true, AuthorizationReference = "R170-OPERADOR-CONTINUO" };
        Assert.False(escopo.EhValida());
        Assert.False(escopo.SnapshotPermitido(Snapshot("3011")));
    }

    [Theory]
    [InlineData("3020")]
    [InlineData("3011 ")]
    public void Recorte_com_centro_de_fora_e_invalido(string centro)
        => Assert.False(Escopo(centro).EhValida());

    [Fact]
    public void Recorte_com_centro_repetido_e_invalido()
        => Assert.False(Escopo("3011", "3011").EhValida());

    // O que o operador pediu em 04/11/2026, por escrito: "no 1002 do guardian tem que ir o centro
    // do contrato, se 3011 vai o 3011 e se 3008 vai 3008".
    [Theory]
    [InlineData("3011")]
    [InlineData("3008")]
    public void Campo_adicional_1002_leva_o_centro_do_proprio_contrato(string centro)
    {
        var preCadastro = MapeadorContratoAcmGuardian.MapearPreCadastro(
            Snapshot(centro), new TransportadoraAcmComprovada("9000", "R170-TRANSPORTADORA"),
            "https://exemplo.invalido/callback");

        var campo = preCadastro.CamposAdicionais.Single(c => c.Numero == 1002);
        Assert.Equal(centro, campo.Valor);
    }

    [Theory]
    [InlineData("3011")]
    [InlineData("3008")]
    public void Campos_confirmados_saem_nos_dois_centros(string centro)
    {
        var campos = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot(centro));
        Assert.Equal("308102", campos.Estado);
        Assert.Equal("309837", campos.TipoLancamento);
        Assert.Equal("OC1207", campos.NumeroReferencia);
    }

    [Fact]
    public void Centro_de_fora_continua_bloqueando_o_cadastro()
    {
        var erro = Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot("3020")));
        Assert.Equal("ACM_SCENARIO_NOT_APPROVED", erro.Decisao);
    }

    // A dupla tipo + pesagem continua fechada nos dois centros: abrir o centro nao abriu o cenario.
    [Theory]
    [InlineData("3008", "AC3P", "S")]
    [InlineData("3008", "AC3S", "E")]
    [InlineData("3011", "AC3P", "S")]
    public void Par_tipo_pesagem_continua_fechado_em_qualquer_centro(string centro, string tipo, string pesagem)
        => Assert.False(Escopo("3011", "3008").SnapshotPermitido(Snapshot(centro, tipo, pesagem)));
}
