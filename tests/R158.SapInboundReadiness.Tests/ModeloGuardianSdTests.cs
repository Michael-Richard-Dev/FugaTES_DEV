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
    private static string Modelo(string tipoPesagem, string contratoAcm, string observacao)
        => $$"""
        {
          "SistemaBalanca": "Toledo",
          "DataPesagem": "05/11/2026 19:19:17",
          "TipoPesagem": "{{tipoPesagem}}",
          "IdPesagem": "0030384",
          "UsuarioPesagem": "leticia.colet",
          "Centro": "3008",
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
    /// O Guardian manda a mesma finalizacao para as duas rotas. Pelo 1000, que no ACM traz o tipo
    /// de contrato ("AC3S"), e pelo ContratoACM preenchido - os dois dizem a mesma coisa.
    /// </summary>
    [Theory]
    [InlineData("AC3S", "0000001175")]
    [InlineData("AC1C", "0000001206")]
    public void A_pesagem_do_acm_e_reconhecida_pelos_dois_caminhos(string tipo, string contrato)
    {
        // Pelo contrato: se o tipo fosse "S", o ContratoACM sozinho ja a separaria.
        Assert.Equal(contrato, LeitorDevolutivaSd.Ler(Modelo("S", contrato, "")).ContratoAcm);

        // Pelo tipo: o 1000 do ACM nao e "S".
        var erro = Assert.Throws<DevolutivaSdInvalidaException>(
            () => LeitorDevolutivaSd.Ler(Modelo(tipo, contrato, "")));
        Assert.Equal(DevolutivaSdCodigos.TipoNaoEhSaida, erro.Codigo);
    }

    [Theory]
    [InlineData("AC3S", "0000001175")]  // separada pelo tipo, no leitor
    [InlineData("S", "0000001175")]     // separada pelo contrato, depois do leitor
    public async Task A_pesagem_do_acm_responde_200_e_nao_grava(string tipo, string contrato)
    {
        var repositorio = new RepositorioFalso();

        var (status, corpo) = await Postar(Modelo(tipo, contrato, ""), repositorio);

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
    /// O caso que ainda nao tem decisao, fixado para ninguem o descobrir por acidente: pesagem sem
    /// pre-cadastro NENHUM nao tem campo 1000, o marcador expande vazio, e isso cai em
    /// CAMPO_AUSENTE - 400, e o Guardian reenvia para sempre.
    ///
    /// Este teste documenta o comportamento de HOJE, nao o desejado. Quando o operador decidir que
    /// tipo vazio e "nao e nossa" (200, nada gravado), este teste muda junto.
    /// </summary>
    [Fact]
    public async Task PENDENTE_tipo_vazio_ainda_responde_400()
    {
        var (status, corpo) = await Postar(Modelo("", "", ""), new RepositorioFalso());

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
