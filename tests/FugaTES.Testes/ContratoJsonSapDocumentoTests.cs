using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Persistencia.Consultas.ContratosAcm;
using FugaTES.WinForms;
using FugaTES.WinForms.Acm;
using Xunit;

namespace FugaTES.Testes;

/// <summary>
/// Acao JSON dos contratos SAP: mapeamento documentado (NTT Data, SERVICO ENVIAR
/// CONTRATO (GET), pagina 6), identidade contrato+item, ausencias declaradas e
/// preservacao de texto, zeros a esquerda, decimais e datas OData.
///
/// A massa reproduz o exemplo real informado (1031 / item 10), sem usar nenhum destes
/// valores como constante de producao.
/// </summary>
public sealed class ContratoJsonSapDocumentoTests
{
    /// <summary>Projecao persistida tal como o canonicalizador a grava: nomes do servico OData.</summary>
    private const string Payload1031Item10 = """
        {
          "SistemaBalanca": "Toledo",
          "NumeroContrato": "1031",
          "TipoContrato": "AC3P",
          "DescricaoTipoContrato": null,
          "DataEmissao": "/Date(1760400000000)/",
          "DataEntregaInicio": null,
          "DataEntregaFim": null,
          "DiasToler": 5,
          "DataLimiteEntrega": null,
          "StatusContrato": null,
          "QuantidadeContrato": 1000.500,
          "QuantidadeSaldoDevolucao": null,
          "IdParceiro": "0020000184",
          "Material": "7000000",
          "TipoPesagem": null,
          "TabelaQualidadeID": null,
          "TabelaQualidadeVersao": null,
          "TabelaQualidadeVersaoValidade": null,
          "Observacao": null,
          "ValorUnitario": null,
          "Centro": "3011",
          "OperacaoContrato": null,
          "CentroOpcional": [],
          "Item": "10",
          "UnidCont": "KG"
        }
        """;

    private static CampoDocumentadoNtt Campo(string documentacao) =>
        ContratoSapDocumentoNtt.Mapear(Payload1031Item10).Single(c => c.CampoDocumentacao == documentacao);

    [Fact]
    public void MapeamentoCobreOsVinteEQuatroCamposDocumentadosNaOrdemDaPagina6()
    {
        Assert.Equal(24, ContratoSapDocumentoNtt.Mapeamento.Length);
        Assert.Equal(("NRCONTRATO", "NumeroContrato"), ContratoSapDocumentoNtt.Mapeamento[0]);
        Assert.Equal(("ITEM", "Item"), ContratoSapDocumentoNtt.Mapeamento[1]);
        Assert.Equal(("DATLIMITENT", "DataLimiteEntrega"), ContratoSapDocumentoNtt.Mapeamento[^1]);
        // Nenhum nome documentado repetido e nenhum campo SAP reaproveitado.
        Assert.Equal(24, ContratoSapDocumentoNtt.Mapeamento.Select(m => m.Documentacao).Distinct().Count());
        Assert.Equal(24, ContratoSapDocumentoNtt.Mapeamento.Select(m => m.Sap).Distinct().Count());
    }

    [Fact]
    public void Contrato1031Item10ExpoeOsCamposDocumentadosPresentes()
    {
        Assert.Equal("1031", Campo("NRCONTRATO").Valor);
        Assert.Equal("10", Campo("ITEM").Valor);
        Assert.Equal("AC3P", Campo("TPCONTRATO").Valor);
        Assert.Equal("3011", Campo("CENTRO").Valor);
        Assert.Equal("7000000", Campo("MATERIAL").Valor);
        Assert.Equal("Toledo", Campo("SISTBALANCA").Valor);
        Assert.Equal("KG", Campo("UNIDCONT").Valor);
        Assert.All(new[] { "NRCONTRATO", "ITEM", "TPCONTRATO", "CENTRO", "MATERIAL", "SISTBALANCA" },
            nome => Assert.True(Campo(nome).Presente));
    }

    [Fact]
    public void IdentificadorPreservaZerosAEsquerdaComoTexto()
    {
        var parceiro = Campo("IDPARCEIRO");
        Assert.Equal("0020000184", parceiro.Valor);
        Assert.Contains("zeros a esquerda", parceiro.Situacao);
        // O JSON documentado tambem preserva o identificador como string.
        using var documentado = JsonDocument.Parse(
            ContratoSapDocumentoNtt.SerializarConformeDocumentacao(Payload1031Item10));
        var idParceiro = documentado.RootElement.GetProperty("IDPARCEIRO");
        Assert.Equal(JsonValueKind.String, idParceiro.ValueKind);
        Assert.Equal("0020000184", idParceiro.GetString());
    }

    [Fact]
    public void DecimalPreservaEscalaExatamenteComoPersistida()
    {
        var quantidade = Campo("QUANCONT");
        Assert.Equal("1000.500", quantidade.Valor);
        Assert.DoesNotContain("1000.5,", quantidade.Valor + ",");
        Assert.Contains("texto bruto", quantidade.Situacao);
        Assert.Equal("5", Campo("DIASTOL").Valor);
    }

    [Fact]
    public void DataODataEhMostradaComoVeioComInstanteUtcMarcadoComoDerivado()
    {
        var emissao = Campo("DTEMISSAO");
        Assert.Equal("/Date(1760400000000)/", emissao.Valor);
        Assert.Contains("DATA ODATA", emissao.Situacao);
        Assert.Contains("2025-10-14T00:00:00Z", emissao.Situacao);
        Assert.Contains("derivado", emissao.Situacao);
        Assert.Contains("sem conversao de fuso", emissao.Situacao);
    }

    [Fact]
    public void DeslocamentoDeFusoNaoEhAplicadoAoInstanteDerivado()
    {
        const string comOffset = """{"NumeroContrato":"1031","DataEmissao":"/Date(1760400000000+0300)/"}""";
        var emissao = ContratoSapDocumentoNtt.Mapear(comOffset).Single(c => c.CampoDocumentacao == "DTEMISSAO");
        Assert.Equal("/Date(1760400000000+0300)/", emissao.Valor);
        // Mesmo instante do caso sem deslocamento: o offset fica no texto e nao e somado.
        Assert.Contains("2025-10-14T00:00:00Z", emissao.Situacao);
    }

    [Fact]
    public void CampoDocumentadoAusenteApareceComoAusenteSemValorInventado()
    {
        var descricao = Campo("DESCTPCONTRATO");
        Assert.False(descricao.Presente);
        Assert.Null(descricao.Valor);
        Assert.Contains("AUSENTE NA ORIGEM", descricao.Situacao);
    }

    [Fact]
    public void GruidprogEMaterialsapSaoDeclaradosSemOrigemENaoRecebemValor()
    {
        var linhas = ContratoSapDocumentoNtt.Mapear(Payload1031Item10);
        foreach (var nome in new[] { "GRUIDPROG", "MATERIALSAP" })
        {
            var campo = linhas.Single(c => c.CampoDocumentacao == nome);
            Assert.False(campo.Presente);
            Assert.Null(campo.Valor);
            Assert.Contains("AUSENTE NA ORIGEM", campo.Situacao);
        }
        // MATERIALSAP nunca e presumido igual a MATERIAL.
        var materialsap = linhas.Single(c => c.CampoDocumentacao == "MATERIALSAP");
        Assert.NotEqual(Campo("MATERIAL").Valor, materialsap.Valor);
        Assert.Contains("NAO equivale a MATERIAL", materialsap.Situacao);
        // E saem como null no JSON documentado, nao omitidos: a ausencia fica visivel.
        using var documentado = JsonDocument.Parse(
            ContratoSapDocumentoNtt.SerializarConformeDocumentacao(Payload1031Item10));
        Assert.Equal(JsonValueKind.Null, documentado.RootElement.GetProperty("GRUIDPROG").ValueKind);
        Assert.Equal(JsonValueKind.Null, documentado.RootElement.GetProperty("MATERIALSAP").ValueKind);
    }

    [Fact]
    public void CamposForaDaDocumentacaoSaoPreservadosEExibidos()
    {
        const string comExtra = """
            {"NumeroContrato":"1031","Item":"10","CampoNovoDoSap":"VALOR-X","OutroExtra":42}
            """;
        var extras = ContratoSapDocumentoNtt.CamposForaDaDocumentacao(comExtra);
        Assert.Contains(extras, e => e.CampoSap == "CampoNovoDoSap" && e.Valor == "VALOR-X");
        Assert.Contains(extras, e => e.CampoSap == "OutroExtra" && e.Valor == "42");
        using var documentado = JsonDocument.Parse(
            ContratoSapDocumentoNtt.SerializarConformeDocumentacao(comExtra));
        var bloco = documentado.RootElement.GetProperty("_CamposForaDaDocumentacao");
        Assert.Equal("VALOR-X", bloco.GetProperty("CampoNovoDoSap").GetString());
    }

    [Fact]
    public void NavegacaoDiferidaNaoEhAnunciadaComoCarregada()
    {
        const string comDeferred = """
            {"NumeroContrato":"1031","to_CentroOpcional":{"__deferred":{"uri":"https://sap.invalid/x"}}}
            """;
        var centros = ContratoSapDocumentoNtt.CamposForaDaDocumentacao(comDeferred)
            .Single(c => c.CampoSap == "to_CentroOpcional");
        Assert.Contains("NAVEGACAO DIFERIDA", centros.Situacao);
        Assert.Contains("NAO carregado", centros.Situacao);
        // Lista vazia tambem nao e anunciada como inexistente na origem.
        var centroOpcional = ContratoSapDocumentoNtt.CamposForaDaDocumentacao(Payload1031Item10)
            .Single(c => c.CampoSap == "CentroOpcional");
        Assert.Contains("LISTA VAZIA", centroOpcional.Situacao);
        Assert.Contains("nao equivale a inexistente", centroOpcional.Situacao);
    }

    [Fact]
    public void RepresentacaoDocumentadaNaoAlteraOPayloadPersistido()
    {
        var antes = Payload1031Item10;
        var documentado = ContratoSapDocumentoNtt.SerializarConformeDocumentacao(antes);
        Assert.Equal(Payload1031Item10, antes);
        // O JSON documentado usa os nomes da documentacao...
        Assert.Contains("NRCONTRATO", documentado);
        Assert.Contains("SISTBALANCA", documentado);
        // ...e o payload persistido continua com os nomes do servico, como o Inc006 exige.
        Assert.Contains("NumeroContrato", antes);
        Assert.DoesNotContain("NRCONTRATO", antes);
    }

    [Fact]
    public void PayloadAusenteOuInvalidoNaoProduzCamposInventados()
    {
        Assert.Throws<ArgumentNullException>(() => ContratoSapDocumentoNtt.Mapear(null!));
        Assert.Throws<ArgumentException>(() => ContratoSapDocumentoNtt.Mapear("   "));
        Assert.ThrowsAny<JsonException>(() => ContratoSapDocumentoNtt.Mapear("{nao-e-json"));
    }

    [Fact]
    public void OrigemProjecaoNuncaEhAnunciadaComoPayloadRecebido()
    {
        Assert.Equal("PROJECAO_PERSISTIDA",
            OrigemJsonContratoTexto.Rotulo(OrigemJsonContrato.ProjecaoPersistida));
        Assert.Equal("RECEBIDO_PELO_FUGATES",
            OrigemJsonContratoTexto.Rotulo(OrigemJsonContrato.RecebidoPeloFugaTES));
        Assert.Equal("CONSULTA_SAP_ATUAL",
            OrigemJsonContratoTexto.Rotulo(OrigemJsonContrato.ConsultaSapAtual));
        var explicacao = OrigemJsonContratoTexto.Explicacao(OrigemJsonContrato.ProjecaoPersistida);
        Assert.Contains("NAO e o payload originalmente", explicacao);
        Assert.Contains("CONSULTA_SAP_ATUAL", OrigemJsonContratoTexto.Rotulo(OrigemJsonContrato.ConsultaSapAtual));
        Assert.Contains("estado ATUAL", OrigemJsonContratoTexto.Explicacao(OrigemJsonContrato.ConsultaSapAtual));
    }

    [Fact]
    public void ConsultaProjetaItemEPayloadSemEscrita()
    {
        var sql = ContratosAcmPostgreSqlReadRepository.ConsultaSql;
        Assert.Contains("v.payload_contrato->>'Item'", sql);
        Assert.Contains("v.payload_contrato::text", sql);
        // Garantias ja existentes preservadas.
        Assert.Contains("payload_contrato->>'Centro' = @centro", sql);
        Assert.Contains("v.versao = identidade.ultima_versao", sql);
        Assert.DoesNotContain("3011", sql);
        Assert.DoesNotContain("insert", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContratoComMultiplosItensMantemItemEIntegracaoDistintos()
    {
        var repositorio = new MemoriaMultiItem();
        var linhas = await new ContratosAcmConsulta(repositorio).ConsultarAsync("3011", default);
        Assert.Equal(2, linhas.Count);
        Assert.All(linhas, l => Assert.Equal("1031", l.NumeroContrato));
        Assert.Equal("10,20", string.Join(',', linhas.Select(l => l.Item).OrderBy(i => i, StringComparer.Ordinal)));
        // Mesmo contrato, itens e integracoes distintas: a linha nao fica ambigua.
        Assert.Equal(2, linhas.Select(l => l.IntegracaoId).Distinct().Count());
        var item10 = linhas.Single(l => l.Item == "10");
        Assert.Contains("\"Item\": \"10\"", item10.PayloadContratoJson!);
        Assert.Equal("10", ContratoSapDocumentoNtt.Mapear(item10.PayloadContratoJson!)
            .Single(c => c.CampoDocumentacao == "ITEM").Valor);
    }

    [Fact]
    public async Task LinhaSemPayloadArmazenadoNaoImpedeAConsulta()
    {
        var linhas = await new ContratosAcmConsulta(new MemoriaMultiItem { SemPayload = true })
            .ConsultarAsync("3011", default);
        Assert.All(linhas, l => Assert.Null(l.PayloadContratoJson));
        // O item segue disponivel para identificar a linha mesmo sem payload.
        Assert.Contains(linhas, l => l.Item == "10");
    }

    [Fact]
    public void GradeExpoeColunaDeItemEAcaoJson()
    {
        Exception? erro = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new FrmContratosAcm(new ContratosAcmConsulta(new MemoriaMultiItem()));
                var grid = (DataGridView)typeof(FrmContratosAcm)
                    .GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var colunas = grid.Columns.Cast<DataGridViewColumn>().ToArray();
                Assert.Contains(colunas, c => c.DataPropertyName == "Item");
                var acao = Assert.Single(colunas, c => c.Name == "json");
                Assert.IsType<DataGridViewButtonColumn>(acao);
                Assert.Equal("JSON", acao.HeaderText);
                // A grade permanece somente leitura.
                Assert.True(grid.ReadOnly);
                Assert.False(grid.AllowUserToAddRows);
            }
            catch (Exception ex) { erro = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(erro);
    }

    [Fact]
    public void JanelaDeJsonSemPayloadNaoReconstroiNemInventa()
    {
        Exception? erro = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var janela = new FrmContratoJson("1031", "10", Guid.Empty, 1, null,
                    OrigemJsonContrato.ProjecaoPersistida);
                var textos = Descendentes(janela).OfType<TextBox>().Select(t => t.Text).ToArray();
                Assert.Contains(textos, t => t.Contains("SEM PAYLOAD ARMAZENADO"));
                Assert.DoesNotContain(textos, t => t.Contains("AC3P"));
                var grades = Descendentes(janela).OfType<DataGridView>().ToArray();
                Assert.All(grades, g => Assert.Empty((IEnumerable<CampoDocumentadoNtt>)g.DataSource!));
            }
            catch (Exception ex) { erro = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(erro);
    }

    [Fact]
    public void JanelaDeJsonDeclaraContratoItemEOrigem()
    {
        Exception? erro = null;
        var thread = new Thread(() =>
        {
            try
            {
                var integracao = Guid.NewGuid();
                using var janela = new FrmContratoJson("1031", "10", integracao, 2,
                    Payload1031Item10, OrigemJsonContrato.ProjecaoPersistida);
                var rotulos = Descendentes(janela).OfType<Label>().Select(l => l.Text).ToArray();
                Assert.Contains(rotulos, t => t.Contains("Contrato 1031") && t.Contains("Item 10"));
                Assert.Contains(rotulos, t => t.Contains(integracao.ToString()));
                Assert.Contains(rotulos, t => t.Contains("PROJECAO_PERSISTIDA"));
                Assert.Contains(rotulos, t => t.Contains("NAO e o payload originalmente"));
                Assert.Contains("1031", janela.Text);
                var textos = Descendentes(janela).OfType<TextBox>().Select(t => t.Text).ToArray();
                Assert.Contains(textos, t => t.Contains("NRCONTRATO"));
                Assert.Contains(textos, t => t.Contains("NumeroContrato"));
            }
            catch (Exception ex) { erro = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(erro);
    }

    private static IEnumerable<Control> Descendentes(Control raiz)
    {
        foreach (Control filho in raiz.Controls)
        {
            yield return filho;
            foreach (var neto in Descendentes(filho)) yield return neto;
        }
    }

    /// <summary>
    /// Repositorio em memoria: nenhuma chamada empresarial externa, nenhum SAP, nenhum banco.
    /// </summary>
    private sealed class MemoriaMultiItem : IContratosAcmReadRepository
    {
        public bool SemPayload;

        public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string Payload(string item) => Payload1031Item10.Replace("\"Item\": \"10\"", $"\"Item\": \"{item}\"");
            ContratoAcmLeitura Linha(string item, long versao) =>
                new(centro, "1031", versao, DateTime.UnixEpoch, null, "PENDENTE",
                    Guid.NewGuid(), "origem-" + item, versao == 2)
                {
                    Item = item,
                    PayloadContratoJson = SemPayload ? null : Payload(item),
                };
            return Task.FromResult<IReadOnlyList<ContratoAcmLeitura>>(
                [Linha("10", 1), Linha("20", 2)]);
        }
    }

    /// <summary>
    /// No modo unificado o botao "Contratos ACM" abre o Monitor ACM. A tela de contratos,
    /// onde vive a acao JSON, ganha entrada propria; sem o monitor ela continua acessivel
    /// pelo botao original e a entrada extra nao aparece.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EntradaDaTelaDeContratosAcompanhaOModoUnificado(bool comMonitorAcm)
    {
        Exception? erro = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = comMonitorAcm
                    ? new FrmIntegracoes(monitorAcm: () => throw new InvalidOperationException("nao acionado no teste"))
                    : new FrmIntegracoes();
                var botao = (Button)typeof(FrmIntegracoes)
                    .GetField("contratosJson", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                Assert.Contains("JSON", botao.Text);
                form.Show();
                try { Assert.Equal(comMonitorAcm, botao.Visible); }
                finally { form.Close(); }
            }
            catch (Exception ex) { erro = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(erro);
    }
}
