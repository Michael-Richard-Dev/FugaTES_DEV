using System.Reflection;
using System.Windows.Forms;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.ProtocolosGuardian;
using FugaTES.WinForms.Guardian;
using Xunit;

namespace FugaTES.Testes;

/// <summary>
/// Monitor de protocolos Guardian REST (ProtocoloPreCadastro): elegibilidade do reenvio
/// decidida por evidencia duravel, JSON por tentativa com origem declarada e ausencia
/// total de POST externo.
/// </summary>
public sealed class ProtocoloPreCadastroMonitorTests
{
    private static readonly Guid Integracao = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static ProtocoloPreCadastroLinha Recuperavel() => new()
    {
        IntegracaoId = Integracao,
        CorrelationId = "CORR-1",
        Contrato = "0000001031",
        Item = "10",
        Referencia = "OC1139",
        EstadoEmpresarial = "FALHA_REPROCESSAVEL",
        EtapaFuncional = "R165U_PROTOCOL_BLOCKED",
        EstadoMotor = "AGUARDANDO_REPROCESSAMENTO",
        Tentativas = 2,
        UltimoErro = "GUARDIAN_REST_TIMEOUT_RECUPERAVEL",
        FalhaReprocessavel = true,
        ContratoConfirmado = true,
        CorrelacaoAmbigua = false,
        Origem = ProtocoloPreCadastroLinha.Real,
        PayloadEnviadoJson = """{"Contrato":{"Codigo":"0000001031"}}""",
    };

    [Fact]
    public void ProtocoloConcluidoBloqueiaReenvio()
    {
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel() with { Protocolo = "4" });
        Assert.False(avaliacao.Recuperavel);
        Assert.Equal(ReenvioProtocolo.ProtocoloJaCriado, avaliacao.Codigo);
    }

    [Fact]
    public void ProtocoloJaExistenteNoGuardianPorTicketBloqueiaReenvio()
    {
        // O ticket sozinho ja comprova resultado externo; nao se exige o numero do protocolo.
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel() with { TicketGuardian = "5" });
        Assert.Equal(ReenvioProtocolo.ProtocoloJaCriado, avaliacao.Codigo);
    }

    [Fact]
    public void ErroRestRecuperavelFicaElegivelSemExecucao()
    {
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel());
        Assert.True(avaliacao.Recuperavel);
        Assert.Equal("ELEGIVEL_SEM_EXECUCAO", avaliacao.Rotulo);
        Assert.False(avaliacao.PodeExecutar);
    }

    [Fact]
    public void Http400NaoRecuperavelExigeCorrecaoComprovada()
    {
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel() with
        {
            FalhaReprocessavel = false, FalhaNaoReprocessavel = true, UltimoErro = "GUARDIAN_REST_HTTP_400",
        });
        Assert.False(avaliacao.Recuperavel);
        Assert.Equal(ReenvioProtocolo.Rejeitado, avaliacao.Codigo);
        Assert.Contains("correcao comprovada", avaliacao.Motivo);
    }

    [Fact]
    public void TimeoutOuResultadoIndeterminadoNaoAutorizaReenvio()
    {
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel() with { ResultadoIndeterminado = true });
        Assert.False(avaliacao.Recuperavel);
        Assert.Equal(ReenvioProtocolo.Indeterminado, avaliacao.Codigo);
    }

    [Fact]
    public void ProcessamentoOuTentativaConcorrenteBloqueia()
    {
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel() with { ProcessamentoAtivo = true });
        Assert.Equal(ReenvioProtocolo.ProcessamentoAtivo, avaliacao.Codigo);
    }

    [Fact]
    public void PayloadAusenteBloqueiaReenvio()
    {
        var avaliacao = ReenvioProtocolo.Avaliar(Recuperavel() with { PayloadEnviadoJson = null });
        Assert.Equal(ReenvioProtocolo.PayloadIndisponivel, avaliacao.Codigo);
    }

    [Fact]
    public void CorrelacaoAusenteOuDivergenteBloqueia()
    {
        Assert.Equal(ReenvioProtocolo.CorrelacaoInvalida,
            ReenvioProtocolo.Avaliar(Recuperavel() with { CorrelacaoAmbigua = true }).Codigo);
        Assert.Equal(ReenvioProtocolo.CorrelacaoInvalida,
            ReenvioProtocolo.Avaliar(Recuperavel() with { CorrelationId = "" }).Codigo);
    }

    [Fact]
    public void ContratoDeOrigemNaoValidadoBloqueia()
    {
        Assert.Equal(ReenvioProtocolo.ContratoNaoValidado,
            ReenvioProtocolo.Avaliar(Recuperavel() with { ContratoConfirmado = false }).Codigo);
    }

    [Fact]
    public void OrigemSinteticaOuNaoAtestadaBloqueia()
    {
        Assert.Equal(ReenvioProtocolo.Sintetico,
            ReenvioProtocolo.Avaliar(Recuperavel() with { Origem = ProtocoloPreCadastroLinha.Simulada }).Codigo);
        // Existencia externa desconhecida e o ultimo portao antes da elegibilidade.
        Assert.Equal(ReenvioProtocolo.ExistenciaDesconhecida,
            ReenvioProtocolo.Avaliar(Recuperavel() with { Origem = ProtocoloPreCadastroLinha.Desconhecida }).Codigo);
    }

    [Fact]
    public void NenhumCenarioAutorizaPostExterno()
    {
        ProtocoloPreCadastroLinha[] casos =
        [
            Recuperavel(),
            Recuperavel() with { Protocolo = "4" },
            Recuperavel() with { ResultadoIndeterminado = true },
            Recuperavel() with { FalhaNaoReprocessavel = true },
            Recuperavel() with { Origem = ProtocoloPreCadastroLinha.Simulada },
        ];
        Assert.All(casos, caso => Assert.False(ReenvioProtocolo.Avaliar(caso).PodeExecutar));
    }

    [Fact]
    public void ConfirmacaoMostraContratoReferenciaEtapaMotivoERisco()
    {
        var linha = Recuperavel();
        var texto = ReenvioProtocolo.TextoConfirmacao(linha, ReenvioProtocolo.Avaliar(linha));
        Assert.Contains("0000001031", texto);
        Assert.Contains("OC1139", texto);
        Assert.Contains(ProtocoloPreCadastro.Rota, texto);
        Assert.Contains("GUARDIAN_REST_TIMEOUT_RECUPERAVEL", texto);
        Assert.Contains("RISCO DE DUPLICIDADE", texto);
        // O contrato SOAP ja criado nao e refeito: a confirmacao diz isso explicitamente.
        Assert.Contains("nao reinicia a etapa SOAP", texto);
        Assert.Contains("nenhum POST externo", texto);
    }

    [Fact]
    public void FiltrosSelecionamPorContratoReferenciaProtocoloESituacao()
    {
        var recuperavel = Recuperavel();
        var concluido = Recuperavel() with { Contrato = "0000009999", Referencia = "OC1140", Protocolo = "4" };
        var indeterminado = Recuperavel() with { Contrato = "0000007777", ResultadoIndeterminado = true };
        ProtocoloPreCadastroLinha[] todas = [recuperavel, concluido, indeterminado];

        bool Aceita(FiltroProtocoloPreCadastro filtro, ProtocoloPreCadastroLinha linha)
            => filtro.Normalizar().Aceita(linha);

        Assert.True(Aceita(new() { Contrato = "1031" }, recuperavel));
        Assert.False(Aceita(new() { Contrato = "1031" }, concluido));
        Assert.True(Aceita(new() { Referencia = "oc1140" }, concluido));
        Assert.True(Aceita(new() { Protocolo = "4" }, concluido));
        Assert.Equal(1, todas.Count(l => Aceita(new() { Situacao = FiltroProtocoloPreCadastro.Concluidos }, l)));
        Assert.Equal(1, todas.Count(l => Aceita(new() { Situacao = FiltroProtocoloPreCadastro.Recuperaveis }, l)));
        Assert.Equal(1, todas.Count(l => Aceita(new() { Situacao = FiltroProtocoloPreCadastro.Indeterminados }, l)));
        Assert.Equal(3, todas.Count(l => Aceita(new(), l)));
    }

    [Fact]
    public void FiltroInvalidoENegadoSemConsultar()
    {
        Assert.Throws<ArgumentException>(() => new FiltroProtocoloPreCadastro { Situacao = "QUALQUER" }.Normalizar());
        Assert.Throws<ArgumentException>(() => new FiltroProtocoloPreCadastro { Contrato = "a\r\nb" }.Normalizar());
    }

    [Fact]
    public void MontagemVinculaPorIntegrationIdEDeclaraAusencias()
    {
        var integracao = new MonitorIntegracao
        {
            IntegracaoId = Integracao, Operacao = "SAP_CONTRATO_TOLEDO", CorrelationId = "CORR-1",
            EstadoMotor = "AGUARDANDO_REPROCESSAMENTO", Documento = "0000001031",
            StatusFuncional = "FALHA_REPROCESSAVEL", EtapaFuncional = "R165U_PROTOCOL_BLOCKED",
            UltimoErro = "GUARDIAN_REST_TIMEOUT", EstadoCorrelacao = "ATIVA",
        };
        var detalhe = new MonitorAcmDetalhe(
            new MonitorDetalhe(
                [new(Guid.NewGuid(), 1, "AUTOMATICO", "ENVIADO", "FALHA", DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch.AddMinutes(1), null)],
                [], []),
            new MonitorAcmDados { Contrato = "0000001031", Referencia = "OC1139", CodigoGuardian = "G1" });

        var linha = ProtocolosPreCadastroPostgreSqlReadRepository.Montar(integracao, detalhe);

        Assert.Equal(Integracao, linha.IntegracaoId);
        Assert.Equal("0000001031", linha.Contrato);
        Assert.Equal("OC1139", linha.Referencia);
        Assert.Equal(1, linha.Tentativas);
        Assert.True(linha.FalhaReprocessavel);
        Assert.True(linha.ContratoConfirmado);
        // Sem origem na persistencia, estes continuam ausentes em vez de inventados.
        Assert.Null(linha.StatusHttp);
        Assert.Null(linha.RespostaJson);
        Assert.Null(linha.Item);
    }

    [Fact]
    public void IntegracaoSemDetalheContinuaListadaComoDesconhecida()
    {
        var integracao = new MonitorIntegracao
        {
            IntegracaoId = Integracao, Operacao = "SAP_CONTRATO_TOLEDO",
            CorrelationId = "CORR-1", EstadoMotor = "PENDENTE",
        };
        var linha = ProtocolosPreCadastroPostgreSqlReadRepository.Montar(integracao, null);
        Assert.Equal(Integracao, linha.IntegracaoId);
        Assert.True(linha.CorrelacaoAmbigua);
        Assert.False(linha.ContratoConfirmado);
        Assert.Equal(0, linha.Tentativas);
    }

    [Fact]
    public void ExibicaoMostraUnknownEmVezDeCampoVazio()
    {
        var exibicao = ProtocoloPreCadastroExibicao.De(new ProtocoloPreCadastroLinha { IntegracaoId = Integracao });
        Assert.Equal(ProtocoloPreCadastro.Desconhecido, exibicao.StatusHttp);
        Assert.Equal(ProtocoloPreCadastro.Desconhecido, exibicao.Contrato);
        Assert.Equal(ProtocoloPreCadastro.Desconhecido, exibicao.Protocolo);
        Assert.Equal(ProtocoloPreCadastro.Desconhecido, exibicao.TentativaEm);
        Assert.Contains("BLOQUEADO", exibicao.Reenvio);
    }

    [Fact]
    public void JsonDeRequisicaoEDeRespostaDeclaramSuaOrigem()
    {
        Exception? erro = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var semCorpo = new FrmProtocoloJson(Recuperavel() with { PayloadEnviadoJson = null });
                Assert.Contains(ProtocoloPreCadastro.NaoArmazenado, Caixa(semCorpo, "JSON enviado ao Guardian REST"));
                Assert.Contains(ProtocoloPreCadastro.NaoArmazenado, Caixa(semCorpo, "JSON da resposta Guardian REST"));

                using var comCorpo = new FrmProtocoloJson(Recuperavel());
                var enviado = Caixa(comCorpo, "JSON enviado ao Guardian REST");
                Assert.Contains("0000001031", enviado);
                Assert.DoesNotContain(ProtocoloPreCadastro.NaoArmazenado, enviado);
                // A resposta continua ausente: nada e reconstruido no lugar dela.
                Assert.Contains(ProtocoloPreCadastro.NaoArmazenado, Caixa(comCorpo, "JSON da resposta Guardian REST"));
                var tecnico = Caixa(comCorpo, "Informacoes tecnicas de execucao");
                Assert.Contains(Integracao.ToString(), tecnico);
                Assert.Contains(ProtocoloPreCadastro.Rota, tecnico);
            }
            catch (Exception ex) { erro = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(erro);
    }

    [Fact]
    public void TokensEAutenticacaoSaoSanitizados()
    {
        var sanitizado = FrmProtocoloJson.Sanitizar(
            """{"Authorization":"Bearer abc.def","token":"segredo","Contrato":"0000001031"}""");
        Assert.DoesNotContain("abc.def", sanitizado);
        Assert.DoesNotContain("segredo", sanitizado);
        Assert.Contains("***REMOVIDO***", sanitizado);
        Assert.Contains("0000001031", sanitizado);
    }

    [Fact]
    public void GradeExpoeColunasEsperadasEAcoesSomenteLeitura()
    {
        Exception? erro = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new FrmProtocolosPreCadastro(new ProtocolosPreCadastroConsulta(new Vazio()));
                var grid = (DataGridView)typeof(FrmProtocolosPreCadastro)
                    .GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var colunas = grid.Columns.Cast<DataGridViewColumn>().ToArray();
                foreach (var esperada in new[] { "Contrato", "Item", "Referencia", "Protocolo", "TentativaEm",
                    "StatusHttp", "EstadoEmpresarial", "Tentativas", "UltimoErro", "Correlacao", "Origem", "Integracao" })
                    Assert.Contains(colunas, c => c.Name == esperada);
                Assert.IsType<DataGridViewButtonColumn>(Assert.Single(colunas, c => c.Name == "json"));
                Assert.IsType<DataGridViewButtonColumn>(Assert.Single(colunas, c => c.Name == "reenviar"));
                Assert.True(grid.ReadOnly);
                Assert.False(grid.AllowUserToAddRows);
                // Clique fora das colunas de acao nao faz nada.
                form.Acionar(-1, 0);
                form.Acionar(0, 0);
            }
            catch (Exception ex) { erro = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(erro);
    }

    [Fact]
    public async Task ConsultaComFalhaNaoInventaLinhas()
    {
        var consulta = new ProtocolosPreCadastroConsulta(new Quebrado());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => consulta.ConsultarAsync(new(), CancellationToken.None));
    }

    [Fact]
    public void CompativelComABaselinePublicadaDoDev110()
    {
        var versao = typeof(FrmProtocolosPreCadastro).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith("1.2.", versao, StringComparison.Ordinal);
    }

    private static string Caixa(Form form, string nome)
        => Todos(form).OfType<TextBox>().First(t => t.AccessibleName == nome).Text;

    private static IEnumerable<Control> Todos(Control raiz)
    {
        foreach (Control filho in raiz.Controls)
        {
            yield return filho;
            foreach (var neto in Todos(filho)) yield return neto;
        }
    }

    private sealed class Vazio : IProtocolosPreCadastroReadRepository
    {
        public Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProtocoloPreCadastroLinha>>([]);
    }

    private sealed class Quebrado : IProtocolosPreCadastroReadRepository
    {
        public Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct)
            => throw new InvalidOperationException("Conexao indisponivel.");
    }
}
