using FugaTES.Aplicacao.Configuracao;
using FugaTES.Persistencia.MotorIntegracao;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// A FABRICA COMUM NAO PODE DEIXAR PASSAR UM PAPEL GUARDADO DE NENHUM AMBIENTE.
///
/// NpgsqlDataSourceFactory.Criar e a fabrica que os registros fora do HostIntegracao usam - a
/// correlacao do receptor, entre outros. Ela pergunta "este papel precisa ser guardado?" e, se
/// sim, desvia para CriarAcm, que exige passfile, porta 5433, schema e a conferencia FISICA de
/// que current_user, session_user e o banco sao os esperados.
///
/// Ate 06/11/2026 essa pergunta era feita so contra os papeis do DEV110. Quando o QAS120 passou a
/// existir para o fluxo SD, um papel fugates_qas_* deixou de ser reconhecido como guardado e caia
/// no NpgsqlDataSource.Create() cru. Nada falhava: a conexao abria, funcionava, e simplesmente
/// nao era conferida.
///
/// O erro e sutil porque a MESMA lista de ambientes tem significados opostos em dois lugares:
/// em ValidateDedicatedBinding ela diz o que o processo PODE alcancar - e alargar afrouxa; aqui
/// ela diz o que precisa ser CONFERIDO - e alargar aperta. Converter um lugar e esquecer o outro
/// abriu o buraco.
/// </summary>
public sealed class FabricaComumPapelGuardadoTests
{
    private static string Cadeia(string banco, string papel)
        => $"Host=127.0.0.1;Port=5433;Database={banco};Search Path=fugates;Username={papel};Passfile=/etc/fugates/pg.pass";

    /// <summary>
    /// O papel de CADA ambiente aprovado e reconhecido como guardado. Se algum deixar de ser,
    /// as conexoes dele passam a abrir sem conferencia de identidade - sem erro nenhum.
    /// </summary>
    [Theory]
    [InlineData("DEV110")]
    [InlineData("QAS120")]
    public void Papeis_de_todo_ambiente_aprovado_sao_guardados(string nome)
    {
        var ambiente = Array.Find(AmbientesBanco.Aprovados, a => a.Nome == nome)!;

        foreach (var papel in new[] { ambiente.PapelAcm, ambiente.PapelSapInbound, ambiente.PapelLeitura })
            Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(papel, AmbientesBanco.Aprovados),
                $"{papel} ({nome}) deixou de ser reconhecido como papel guardado");
    }

    /// <summary>
    /// A fabrica comum NAO entrega fonte crua para um papel guardado: ela desvia para CriarAcm,
    /// que recusa esta cadeia por faltar o que ele exige. A recusa E a prova do desvio - uma
    /// fonte devolvida sem erro significaria que a guarda foi pulada.
    /// </summary>
    [Theory]
    [InlineData("fugates_acm_hml_app", "fugates_dev")]
    [InlineData("fugates_qas_acm_app", "fugates_qas")]
    [InlineData("fugates_qas_sap_inbound_app", "fugates_qas")]
    public void Fabrica_comum_desvia_papel_guardado_para_a_guarda(string papel, string banco)
    {
        // Sem Passfile: CriarAcm recusa. Se Criar tivesse entregue uma fonte crua, nao haveria erro.
        var semPassfile = $"Host=127.0.0.1;Port=5433;Database={banco};Search Path=fugates;Username={papel};Password=x";

        var erro = Record.Exception(() => NpgsqlDataSourceFactory.Criar(semPassfile));

        Assert.NotNull(erro);
        Assert.Equal("R165R_EXPLICIT_ACM_PASSFILE_REQUIRED", erro!.Message);
    }

    /// <summary>
    /// O par continua indivisivel: reconhecer o papel do QAS nao faz o banco do DEV servir para
    /// ele. Alargar o que e CONFERIDO nao pode alargar o que e ACEITO.
    /// </summary>
    [Theory]
    [InlineData("fugates_qas_acm_app", "fugates_dev")]
    [InlineData("fugates_acm_hml_app", "fugates_qas")]
    public void Papel_de_um_ambiente_com_banco_do_outro_continua_recusado(string papel, string banco)
    {
        var erro = Record.Exception(() => NpgsqlDataSourceFactory.Criar(Cadeia(banco, papel)));

        Assert.NotNull(erro);
        Assert.Equal("R165R_ACM_CONNECTION_METADATA_REQUIRED", erro!.Message);
    }

    /// <summary>Um papel que nao e de nenhum ambiente aprovado continua sem desvio.</summary>
    [Theory]
    [InlineData("postgres")]
    [InlineData("fugates_qas_app")]
    [InlineData("fugates_dev_app")]
    public void Papel_nao_guardado_continua_sem_desvio(string papel)
        => Assert.False(NpgsqlDataSourceFactory.EhRoleGuardada(papel, AmbientesBanco.Aprovados));
}
