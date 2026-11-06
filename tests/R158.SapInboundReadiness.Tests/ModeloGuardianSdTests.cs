using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O MODELO QUE ESTA CONFIGURADO NO GUARDIAN, em 06/11/2026, com os marcadores expandidos.
///
/// Nao e um payload inventado: e a forma exata do JSON que o operador publicou, campo por campo e
/// na mesma ordem. Ele muda do lado de la, sem passar por aqui, e quando mudar e este arquivo que
/// tem de acusar - foi o que faltou em 05/11/2026, quando "Placa Carreta" chegou sem aspas e
/// quando o 1006 deixou de trazer lacre.
///
/// Dois marcadores mandam no destino da pesagem:
///
///   #CAT_VALOR_1000#  -> TipoPesagem. Nos gravamos "S" no pre-cadastro SD; o ACM grava o TIPO DE
///                        CONTRATO dele no MESMO campo 1000 (MapeadorContratoAcmGuardian).
///   #CODIGO_CONTRATO# -> ContratoACM, dentro de to_Pesagem. Devolvido ao modelo em 06/11/2026
///                        por recomendacao minha: era o separador explicito entre os fluxos.
///
/// Os dois tem de concordar que a pesagem do ACM nao e do SD. Um so ja barraria, e e de proposito
/// que sao dois: o 1000 e compartilhado com outro fluxo, e um valor inesperado nele nao pode ser
/// a unica coisa entre um recebimento e uma remessa.
/// </summary>
public sealed class ModeloGuardianSdTests
{
    /// <summary>
    /// O modelo com os marcadores substituidos. A quebra de linha da observacao vai CRUA, como o
    /// Guardian a manda - o usuario da balanca da Enter para separar o lacre de carga do de
    /// testemunha, e o JSON nao a escapa.
    /// </summary>
    /// <remarks>
    /// <paramref name="centro"/> existe porque ele TAMBEM sai de campo adicional - o 1001, que so
    /// o pre-cadastro do SD grava. Numa pesagem do ACM ele chega vazio, e modelar isso errado foi
    /// o que escondeu, em 06/11/2026, que a pesagem do ACM morria em CAMPO_AUSENTE antes de
    /// qualquer verificacao de dono.
    /// </remarks>
    private static string Modelo(string tipoPesagem, string contratoAcm, string observacao,
        string centro = "3008")
        => $$"""
        {
          "SistemaBalanca": "Toledo",
          "DataPesagem": "05/11/2026 19:19:17",
          "TipoPesagem": "{{tipoPesagem}}",
          "IdPesagem": "0030384",
          "UsuarioPesagem": "leticia.colet",
          "Centro": "{{centro}}",
          "PesoBruto": "56100,0",
          "PesoTara": "19500,0",
          "PesoAntesDesconto": "36600,0",
          "PesoAposDesconto": "36600,0",
          "NomeMotorista": "CRISTIANO MASETTO",
          "CpfMotorista": "02044601079",
          "IdTransportadora": "14300001",
          "NomeTransportadora": "Transporte Jabuti",
          "Placa": "MLH4G82",
          "Placa Carreta": "JAO0G12",
          "Observacao": "{{observacao}}",
          "CamposAdicionais": [
            { "numero": 1003, "valor": "4308" },
            { "numero": 1009, "valor": "10" }
          ],
          "to_QualidPes": [
            { "Tipo": "UMIDADE", "ValorAferido": "0,0" },
            { "Tipo": "", "ValorAferido": "" }
          ],
          "to_Pesagem": [
            {
              "Indice": 1,
              "NrIndiceTotal": 1,
              "IdParceiroEntrega": "",
              "PercentualDesdobro": "100",
              "PesoAntesDesconto": "36600,0",
              "PesoAposDesconto": "36600,0",
              "ContratoACM": "{{contratoAcm}}"
            }
          ]
        }
        """;

    /// <summary>Uma venda: 1000 traz o "S" que nos mesmos gravamos, e nao ha contrato do ACM.</summary>
    private static string Venda(string observacao = "148594,148595,148596,148597 \r\n59187,59188,59189")
        => Modelo("S", "", observacao);

    // --- a venda, lida inteira ---------------------------------------------------------------

    [Fact]
    public void A_venda_e_lida_com_correlacao_e_pesos()
    {
        var d = LeitorDevolutivaSd.Ler(Venda());

        Assert.Equal("0030384", d.IdPesagem);
        Assert.Equal("3008", d.Centro);
        Assert.Equal(36600m, d.PesoLiquido);
        Assert.True(d.TemCorrelacao);     // 1003 e 1009 continuam no modelo
        Assert.Null(d.ContratoAcm);       // ContratoACM vazio nao e contrato
    }

    [Fact]
    public void A_venda_rende_os_dois_tipos_de_lacre_e_o_resto_do_complemento()
    {
        var c = MapeadorTextoNfeSd.LerComplemento(Venda());

        Assert.Equal("148594,148595,148596,148597", c.Lacre);
        Assert.Equal("59187,59188,59189", c.Testemunha);
        Assert.Equal("MLH4G82 / JAO0G12", c.Placas);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
        Assert.Equal("CRISTIANO MASETTO", c.Motorista);
        Assert.Equal("02044601079", c.Cpf);
    }

    /// <summary>
    /// O texto que a ordem manda, com os espacos vazios, preenchido por este modelo. E a linha que
    /// sai impressa na NF-e - a mesma forma do print de 06/11/2026.
    /// </summary>
    [Fact]
    public void O_texto_da_nfe_sai_com_cada_lacre_no_seu_rotulo()
    {
        var daOrdem = "VENDEDOR: JOSE\nLacres:  " + TextoNfeSd.RotuloTestemunha + "  Mot.:  CPF:  Placas: ";

        var texto = MapeadorTextoNfeSd.Compor(daOrdem, MapeadorTextoNfeSd.LerComplemento(Venda()));

        Assert.Contains("Lacres: 148594,148595,148596,148597 " + TextoNfeSd.RotuloTestemunha, texto);
        Assert.Contains(TextoNfeSd.RotuloTestemunha + " 59187,59188,59189 Mot.:", texto);
        Assert.Contains("Mot.: CRISTIANO MASETTO", texto);
        Assert.Contains("Placas: MLH4G82 / JAO0G12", texto);
        // O que veio da ordem nao e tocado.
        Assert.Contains("VENDEDOR: JOSE", texto);
    }

    // --- a pesagem do ACM, que chega na MESMA rota --------------------------------------------

    /// <summary>
    /// A PESAGEM DO ACM COMO ELA REALMENTE CHEGA: tipo com o contrato do ACM no 1000, ContratoACM
    /// preenchido e **Centro VAZIO** - porque o centro sai do campo adicional 1001, e o
    /// pre-cadastro do ACM grava 1000, 1002 e 1003, nunca o 1001.
    ///
    /// Era aqui que estava o defeito ate 06/11/2026: o centro era exigido ANTES do tipo, e a
    /// pesagem morria em CAMPO_AUSENTE com 400 - reenvio eterno - sem nunca chegar na verificacao
    /// de dono. Meu proprio teste escondia isso, porque eu preenchia o centro no caso do ACM.
    /// </summary>
    [Theory]
    [InlineData("AC3S", "0000001175")]
    [InlineData("AC1C", "0000001206")]
    public void A_pesagem_do_acm_e_reconhecida_mesmo_sem_centro(string tipo, string contrato)
    {
        var erro = Assert.Throws<DevolutivaSdInvalidaException>(
            () => LeitorDevolutivaSd.Ler(Modelo(tipo, contrato, "", centro: "")));

        Assert.Equal(DevolutivaSdCodigos.TipoNaoEhSaida, erro.Codigo);
        Assert.True(DevolutivaSdCodigos.NaoEhDoSd(erro.Codigo));
        Assert.Equal("0030384", erro.IdPesagem);
    }

    /// <summary>
    /// O segundo caminho, independente do primeiro: tipo "S" e ContratoACM preenchido. Aqui o
    /// documento atravessa o leitor inteiro e quem separa e o contrato, depois.
    /// </summary>
    [Fact]
    public void O_contrato_acm_separa_mesmo_quando_o_tipo_diz_venda()
        => Assert.Equal("0000001175", LeitorDevolutivaSd.Ler(Modelo("S", "0000001175", "")).ContratoAcm);

    [Theory]
    [InlineData("AC3S", "0000001175", "")]        // ACM real: separada pelo tipo, sem centro
    [InlineData("S", "0000001175", "3008")]       // separada pelo contrato, depois do leitor
    public async Task A_pesagem_do_acm_responde_200_e_nao_grava(string tipo, string contrato, string centro)
    {
        var repositorio = new RepositorioFalso();

        var (status, corpo) = await Postar(Modelo(tipo, contrato, "", centro), repositorio);

        Assert.Equal(200, status);
        Assert.Equal("OK", corpo.GetProperty("mensagem").GetString());
        Assert.Equal("NAO_E_DO_SD", corpo.GetProperty("status").GetString());
        Assert.Empty(repositorio.Gravadas);
    }

    [Fact]
    public async Task A_venda_do_modelo_entra_e_e_gravada()
    {
        var repositorio = new RepositorioFalso();

        var (status, corpo) = await Postar(Venda(), repositorio);

        Assert.Equal(200, status);
        Assert.Equal("RECEBIDA", corpo.GetProperty("status").GetString());
        Assert.True(corpo.GetProperty("persisted").GetBoolean());
        Assert.Equal("0030384", Assert.Single(repositorio.Gravadas).IdPesagem);
    }

    /// <summary>
    /// PESAGEM AVULSA DA BALANCA: sem pre-cadastro nenhum, nem SD nem ACM. Nenhum campo adicional
    /// existe, entao tipo, centro, 1003 e 1009 chegam todos vazios.
    ///
    /// Nos SEMPRE gravamos "S" no 1000, entao tipo vazio quer dizer "nao nasceu de pre-cadastro
    /// do SD". Nao e documento errado, e documento alheio: 200, nada gravado, e uma linha no
    /// journal - decidido pelo operador em 06/11/2026. Sem isso, era 400 e reenvio eterno.
    /// </summary>
    [Fact]
    public async Task A_pesagem_sem_pre_cadastro_responde_200_e_nao_grava()
    {
        var repositorio = new RepositorioFalso();

        var (status, corpo) = await Postar(Modelo("", "", "", centro: ""), repositorio);

        Assert.Equal(200, status);
        Assert.Equal("OK", corpo.GetProperty("mensagem").GetString());
        Assert.Equal("NAO_E_DO_SD", corpo.GetProperty("status").GetString());
        Assert.Contains("sem pré-cadastro", corpo.GetProperty("detalhe").GetString()!);
        Assert.Empty(repositorio.Gravadas);
    }

    [Fact]
    public void A_pesagem_sem_pre_cadastro_leva_o_id_para_o_journal()
    {
        var erro = Assert.Throws<DevolutivaSdInvalidaException>(
            () => LeitorDevolutivaSd.Ler(Modelo("", "", "", centro: "")));

        Assert.Equal(DevolutivaSdCodigos.TipoAusente, erro.Codigo);
        // Sem o id, a linha do journal diria que algo chegou sem dizer o que.
        Assert.Equal("0030384", erro.IdPesagem);
    }

    /// <summary>
    /// O LIMITE da permissividade: campo faltando numa pesagem QUE E NOSSA continua sendo 400 com
    /// a causa. Trocar isso faria o Guardian desistir de reenviar uma venda que ele deveria
    /// reenviar depois de corrigida.
    /// </summary>
    [Fact]
    public async Task Venda_com_campo_faltando_continua_sendo_recusada()
    {
        var (status, corpo) = await Postar(Modelo("S", "", "", centro: ""), new RepositorioFalso());

        Assert.Equal(400, status);
        Assert.Equal("SD_DEVOLUTIVA_CAMPO_AUSENTE", corpo.GetProperty("codigo").GetString());
    }

    // --- andaime ------------------------------------------------------------------------------

    private sealed class RepositorioFalso : IDevolutivasSdRepositorio
    {
        public List<DevolutivaPesagemSd> Gravadas { get; } = [];

        public Task<IReadOnlyList<DevolutivaSdLinha>> ListarAsync(int limite, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DevolutivaSdLinha>>([]);

        public Task RegistrarAsync(DevolutivaPesagemSd devolutiva, CancellationToken ct)
        {
            Gravadas.Add(devolutiva);
            return Task.CompletedTask;
        }
    }

    private static async Task<(int Status, JsonElement Corpo)> Postar(
        string corpo, IDevolutivasSdRepositorio repositorio)
    {
        var servicos = new ServiceCollection();
        servicos.AddSingleton(repositorio);
        var contexto = new DefaultHttpContext { RequestServices = servicos.BuildServiceProvider() };
        contexto.Request.Method = "POST";
        contexto.Request.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(corpo);
        contexto.Request.Body = new MemoryStream(bytes);
        contexto.Request.ContentLength = bytes.Length;
        contexto.Response.Body = new MemoryStream();

        await DevolutivaSdEndpoint.HandleAsync(contexto);

        contexto.Response.Body.Position = 0;
        using var leitor = new StreamReader(contexto.Response.Body);
        using var doc = JsonDocument.Parse(await leitor.ReadToEndAsync());
        return (contexto.Response.StatusCode, doc.RootElement.Clone());
    }
}
