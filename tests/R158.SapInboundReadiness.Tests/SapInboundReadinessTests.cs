using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Consultas.ContratosAcm;
using FugaTES.Persistencia.ContratosSapToledo;
using Microsoft.Extensions.Configuration;

namespace R158.SapInboundReadiness.Tests;

// R158: prontidao do recebimento SAP no DEV unificado. Tudo OFFLINE:
// nenhum POST SAP, nenhuma chamada Guardian, nenhuma conexao de banco, nenhum worker.
// Os guards SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED e SAP_INBOUND_SCOPE_NOT_AUTHORIZED
// sao exercitados, nunca removidos.
public sealed class SapInboundReadinessTests
{
    // ---------- apoio ----------

    private static ContratoSapToledoSnapshot Snapshot(
        string numero = "0000987001", string? item = "10", string? centro = "3011",
        string? parceiro = "0000500123", string? material = "000000000000100045",
        string? status = "T", decimal? quantidade = 1000.000m)
        => new("TOLEDO", numero, "AC3P", "Contrato de Compra", "/Date(1761523200000)/",
            "/Date(1761523200000)/", "/Date(1764201600000)/", 5, "/Date(1764288000000)/",
            status, quantidade, 0m, parceiro, material, "E",
            "QT01", "1", "/Date(1761523200000)/", "Observacao preservada",
            12.345m, centro, "10", [centro]) { Item = item, UnidCont = "KG" };

    private sealed class StoreFake : IContratoSapToledoVersaoStore
    {
        private readonly Dictionary<string, List<(string Fingerprint, Guid Id, long Versao)>> porContrato = new(StringComparer.Ordinal);
        public int Gravacoes;
        public Exception? FalharCom;
        public Exception? FalharNoH4;

        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,
            string fingerprint, string payload, string correlationId, CancellationToken ct)
        {
            if (FalharNoH4 is not null) throw FalharNoH4;      // H4 nao criado: nada e persistido
            if (FalharCom is not null) throw FalharCom;         // falha de persistencia
            if (!porContrato.TryGetValue(snapshot.NumeroContrato, out var versoes))
                porContrato[snapshot.NumeroContrato] = versoes = [];
            var existente = versoes.FirstOrDefault(v => v.Fingerprint == fingerprint);
            if (existente.Fingerprint is not null)
                return Task.FromResult(new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.JaExistente,
                    snapshot.NumeroContrato, existente.Versao, fingerprint, existente.Id, snapshot));
            var nova = (fingerprint, Guid.NewGuid(), versoes.Count + 1L);
            versoes.Add(nova);
            Gravacoes++;
            return Task.FromResult(new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,
                snapshot.NumeroContrato, nova.Item3, fingerprint, nova.Item2, snapshot));
        }
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<VersaoContratoSapToledo?>(null);
        public Task<bool> TemPredecessorNaoTerminalAsync(string numero, long versao, CancellationToken ct)
            => Task.FromResult(false);
    }

    private static AceitarContratoSapToledoDuravelCasoDeUso Caso(StoreFake store)
        => new(new ContratoSapToledoCanonicalizer(), store);

    private static string Fonte(string nome)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Directory.EnumerateFiles(Path.Combine(dir!, "src"), nome, SearchOption.AllDirectories).Single());
    }

    private static string RaizRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    // ---------- 1) novo contrato SAP valido ----------

    [Fact]
    public async Task Novo_contrato_sap_valido_e_aceito_com_versao_e_integrationid()
    {
        var store = new StoreFake();
        var r = await Caso(store).ExecutarAsync(Snapshot(), "corr-0001", default);
        Assert.Equal("0000987001", r.NumeroContrato);   // zeros a esquerda preservados
        Assert.Equal(1, r.Versao);
        Assert.False(r.Reentrega);
        Assert.Equal(1, store.Gravacoes);
    }

    [Fact]
    public void O_contrato_de_dados_sap_preserva_todos_os_campos_recebidos()
    {
        var canonico = new ContratoSapToledoCanonicalizer().Canonicalizar(Snapshot());
        var json = canonico.PayloadCanonicoJson;
        foreach (var campo in new[] { "NumeroContrato", "Item", "TipoContrato", "DescricaoTipoContrato",
            "DataEmissao", "IdParceiro", "Centro", "Material", "SistemaBalanca", "TipoPesagem",
            "DataEntregaInicio", "DataEntregaFim", "UnidCont", "QuantidadeContrato",
            "QuantidadeSaldoDevolucao", "TabelaQualidadeID", "TabelaQualidadeVersao",
            "TabelaQualidadeVersaoValidade", "Observacao", "OperacaoContrato", "ValorUnitario",
            "StatusContrato", "DiasToler", "DataLimiteEntrega" })
            Assert.Contains("\"" + campo + "\"", json, StringComparison.Ordinal);
        // Identificadores com zeros a esquerda, decimais e datas OData sao preservados literalmente.
        Assert.Contains("\"0000987001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"0000500123\"", json, StringComparison.Ordinal);
        Assert.Contains("/Date(1761523200000)/", json, StringComparison.Ordinal);
        Assert.Contains("12.345", json, StringComparison.Ordinal);
        // Nenhum campo inventado entra no payload canonico.
        foreach (var inventado in new[] { "GRUIDPROG", "MATERIALSAP" })
            Assert.DoesNotContain(inventado, json, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 2) contrato com multiplos itens ----------

    [Fact]
    public async Task Contrato_com_multiplos_itens_gera_uma_versao_e_integracao_por_item()
    {
        var store = new StoreFake();
        var caso = Caso(store);
        var item10 = await caso.ExecutarAsync(Snapshot(item: "10"), "corr-i10", default);
        var item20 = await caso.ExecutarAsync(Snapshot(item: "20"), "corr-i20", default);
        Assert.Equal(item10.NumeroContrato, item20.NumeroContrato);
        Assert.NotEqual(item10.Versao, item20.Versao);        // itens distintos = versoes distintas
        Assert.Equal(2, store.Gravacoes);
        // E o item e preservado na projecao, nunca suposto.
        Assert.Equal("20", Snapshot(item: "20").Item);
    }

    // ---------- 3) contrato de centro diferente de 3011 ----------

    [Fact]
    public async Task Contrato_de_outro_centro_e_persistido_e_fica_visivel_por_todos_os_centros()
    {
        var store = new StoreFake();
        var r = await Caso(store).ExecutarAsync(Snapshot(centro: "3020"), "corr-3020", default);
        Assert.Equal(1, r.Versao);
        // A leitura nao pode esconder o contrato: o sentinela dispensa a igualdade de centro.
        Assert.True(ContratosAcmConsulta.EhTodosOsCentros(ContratosAcmConsulta.TodosOsCentros));
        Assert.Contains("@todos or v.payload_contrato->>'Centro' = @centro",
            ContratosAcmPostgreSqlReadRepository.ConsultaSql, StringComparison.Ordinal);
        // O centro continua parametrizado: nenhuma interpolacao entrou na consulta.
        Assert.DoesNotContain("'3011'", ContratosAcmPostgreSqlReadRepository.ConsultaSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_sentinela_de_todos_os_centros_chega_normalizado_ao_repositorio()
    {
        string? recebido = null;
        var consulta = new ContratosAcmConsulta(new RepositorioEspiao(c => recebido = c));
        await consulta.ConsultarAsync("todos", default);
        Assert.Equal(ContratosAcmConsulta.TodosOsCentros, recebido);
        await consulta.ConsultarAsync(" 3020 ", default);
        Assert.Equal("3020", recebido);
        // Centro vazio continua sendo recusado: a tela nao consulta sem escopo.
        await Assert.ThrowsAsync<ArgumentException>(() => consulta.ConsultarAsync("  ", default));
    }

    private sealed class RepositorioEspiao(Action<string> observar) : IContratosAcmReadRepository
    {
        public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
        { observar(centro); return Task.FromResult<IReadOnlyList<ContratoAcmLeitura>>([]); }
    }

    // ---------- 4) contrato duplicado com mesmo fingerprint ----------

    [Fact]
    public async Task Reentrega_com_mesmo_fingerprint_e_idempotente_e_nao_duplica()
    {
        var store = new StoreFake();
        var caso = Caso(store);
        var primeiro = await caso.ExecutarAsync(Snapshot(), "corr-a", default);
        var segundo = await caso.ExecutarAsync(Snapshot(), "corr-b", default);
        Assert.False(primeiro.Reentrega);
        Assert.True(segundo.Reentrega);
        Assert.Equal(primeiro.Versao, segundo.Versao);
        Assert.Equal(1, store.Gravacoes);   // nenhuma segunda gravacao
    }

    // ---------- 5) mesmo contrato com payload alterado ----------

    [Fact]
    public async Task Payload_alterado_gera_nova_versao_do_mesmo_contrato()
    {
        var store = new StoreFake();
        var caso = Caso(store);
        var v1 = await caso.ExecutarAsync(Snapshot(quantidade: 1000.000m), "corr-v1", default);
        var v2 = await caso.ExecutarAsync(Snapshot(quantidade: 1500.000m), "corr-v2", default);
        Assert.Equal(v1.NumeroContrato, v2.NumeroContrato);
        Assert.Equal(1, v1.Versao);
        Assert.Equal(2, v2.Versao);
        Assert.False(v2.Reentrega);
        Assert.Equal(2, store.Gravacoes);
    }

    // ---------- 6/7) falha de H4 e de persistencia com rollback integral ----------

    [Fact]
    public async Task Falha_de_h4_nao_devolve_aceite_e_nada_e_persistido()
    {
        var store = new StoreFake { FalharNoH4 = new InvalidOperationException("H4 integration field") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Caso(store).ExecutarAsync(Snapshot(), "corr-h4", default));
        Assert.Equal(0, store.Gravacoes);
    }

    [Fact]
    public async Task Falha_de_persistencia_nao_devolve_aceite()
    {
        var store = new StoreFake { FalharCom = new InvalidOperationException("SYNTHETIC_PERSISTENCE_FAILURE") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Caso(store).ExecutarAsync(Snapshot(), "corr-p", default));
        Assert.Equal(0, store.Gravacoes);
    }

    [Fact]
    public void Contrato_versao_h4_e_integrationid_sao_gravados_na_mesma_transacao_com_commit()
    {
        var fonte = Fonte("ContratoSapToledoVersaoStorePostgreSql.cs");
        // Uma transacao explicita envolve identidade, H4 e versao, e o COMMIT e o ultimo passo.
        Assert.Contains("BeginTransactionAsync(IsolationLevel.ReadCommitted", fonte, StringComparison.Ordinal);
        var h4 = fonte.IndexOf("RegistrarCandidatoNaTransacaoAsync", StringComparison.Ordinal);
        var versao = fonte.IndexOf("insert into contrato_sap_toledo_versao", StringComparison.Ordinal);
        var commit = fonte.LastIndexOf("await tx.CommitAsync(ct)", StringComparison.Ordinal);
        Assert.True(h4 > 0 && versao > h4, "H4 deve ser registrado antes da versao, na mesma transacao");
        Assert.True(commit > versao, "o COMMIT deve ocorrer depois de identidade, H4 e versao");
        // Qualquer falha desfaz tudo: nao existe caminho de aceite sem COMMIT.
        Assert.Contains("catch { await tx.RollbackAsync(ct); throw; }", fonte, StringComparison.Ordinal);
        // E o H4 ausente e falha dura, nao silenciosa.
        Assert.Contains("!=ResultadoRegistroIntegracao.Criada", fonte, StringComparison.Ordinal);
    }

    // ---------- 8) escopo ACM ausente ou expirado ----------

    [Fact]
    public void Escopo_acm_ausente_bloqueia_a_composicao_produtiva()
    {
        var erro = Assert.Throws<InvalidOperationException>(() =>
            SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(), Config()));
        Assert.Equal("SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED", erro.Message);
    }

    [Fact]
    public void Janela_expirada_ou_fora_do_intervalo_nao_esta_aberta()
    {
        var janela = Janela();
        Assert.True(janela.EstaAberta(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));
        Assert.False(janela.EstaAberta(new DateTimeOffset(2026, 10, 28, 9, 59, 59, TimeSpan.Zero)));
        Assert.False(janela.EstaAberta(new DateTimeOffset(2026, 10, 28, 18, 0, 1, TimeSpan.Zero)));
    }

    [Fact]
    public void A_janela_recusa_centro_parceiro_material_e_historico_fora_do_escopo()
    {
        var janela = Janela();
        Assert.True(janela.SnapshotPermitido(Snapshot()));
        Assert.False(janela.SnapshotPermitido(Snapshot(centro: "3020")));
        Assert.False(janela.SnapshotPermitido(Snapshot(parceiro: "0000999999")));
        Assert.False(janela.SnapshotPermitido(Snapshot(material: "000000000000999999")));
        Assert.False(janela.SnapshotPermitido(Snapshot(status: "A")));
        // Historico do 5432 nunca e readmitido, com ou sem zeros a esquerda.
        foreach (var historico in new[] { "1139", "0000001139", "1155", "0000001031" })
            Assert.False(janela.SnapshotPermitido(Snapshot(numero: historico)));
    }

    // ---------- 9) credenciais invalidas ----------

    [Fact]
    public void Credenciais_basic_ausentes_bloqueiam_a_composicao()
    {
        var erro = Assert.Throws<InvalidOperationException>(() =>
            SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(),
                Config(d => { d.Remove("SapInbound:BasicAuth:Username"); d.Remove("SapInbound:BasicAuth:Password"); })));
        Assert.Equal("SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED", erro.Message);
    }

    [Fact]
    public void Modo_de_seguranca_ausente_ou_invalido_bloqueia_a_composicao()
    {
        foreach (var modo in new[] { null, "", "Open", "Loopback" })
        {
            var erro = Assert.Throws<InvalidOperationException>(() =>
                SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(),
                    Config(d => d["SapContractInbound:SecurityMode"] = modo)));
            Assert.Equal("SAP_INBOUND_SECURITY_MODE_REQUIRED", erro.Message);
        }
    }

    // ---------- 10) payload contratual invalido ----------

    [Fact]
    public async Task Correlation_id_ausente_e_recusado_antes_de_qualquer_persistencia()
    {
        var store = new StoreFake();
        await Assert.ThrowsAsync<ArgumentException>(() => Caso(store).ExecutarAsync(Snapshot(), "   ", default));
        Assert.Equal(0, store.Gravacoes);
    }

    [Fact]
    public void Numero_de_contrato_invalido_e_recusado_pela_janela()
    {
        var janela = Janela();
        foreach (var invalido in new[] { "", "   ", "ABC", "12 34" })
            Assert.False(janela.SnapshotPermitido(Snapshot(numero: invalido)));
    }

    // ---------- 11) projecao consultavel apos COMMIT ----------

    [Fact]
    public void A_leitura_projeta_contrato_item_json_status_e_integrationid()
    {
        var sql = ContratosAcmPostgreSqlReadRepository.ConsultaSql;
        foreach (var fragmento in new[] { "v.numero_contrato", "v.versao", "v.integracao_id",
            "payload_contrato->>'Item'", "v.payload_contrato::text", "payload_contrato->>'StatusContrato'",
            "i.estado", "i.correlation_id" })
            Assert.Contains(fragmento, sql, StringComparison.Ordinal);
        // A projecao so e visivel quando contrato, identidade e H4 existem: join interno nas tres.
        Assert.Contains("from fugates.contrato_sap_toledo_versao v", sql, StringComparison.Ordinal);
        Assert.Contains("join fugates.contrato_sap_toledo_identidade", sql, StringComparison.Ordinal);
        Assert.Contains("join fugates.integracao i", sql, StringComparison.Ordinal);
        // Leitura estritamente somente leitura.
        Assert.Contains("SET TRANSACTION READ ONLY", Fonte("ContratosAcmPostgreSqlReadRepository.cs"), StringComparison.Ordinal);
    }

    // ---------- 12/13) grade DEV110: atualizacao e JSON persistido ----------

    [Fact]
    public void A_grade_atualiza_sozinha_em_dez_segundos_e_tambem_sob_demanda()
    {
        var fonte = Fonte("FrmContratosAcm.cs");
        Assert.Contains("Interval = 10000", fonte, StringComparison.Ordinal);
        Assert.Contains("atualizacaoAutomatica", fonte, StringComparison.Ordinal);
        Assert.Contains("atualizar.Click", fonte, StringComparison.Ordinal);
        // Nem o timer nem a tela disparam reenvio, POST SAP ou chamada Guardian.
        foreach (var proibido in new[] { "GravarAsync", "SendAsync(", "HttpClient" })
            Assert.DoesNotContain(proibido, fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void A_opcao_todos_os_centros_existe_na_tela()
    {
        var fonte = Fonte("FrmContratosAcm.cs");
        Assert.Contains("Todos os centros", fonte, StringComparison.Ordinal);
        Assert.Contains("ContratosAcmConsulta.TodosOsCentros", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void O_json_do_contrato_vem_da_projecao_persistida()
    {
        // O payload acompanha a linha, portanto o botao JSON nao depende de consulta ao vivo.
        var linha = new ContratoAcmLeitura("3011", "0000987001", 1, DateTime.UtcNow, "T", "PENDENTE",
            Guid.NewGuid(), "corr-1", true) { Item = "10", PayloadContratoJson = "{\"NumeroContrato\":\"0000987001\"}" };
        Assert.Equal("10", linha.Item);
        Assert.Contains("0000987001", linha.PayloadContratoJson!, StringComparison.Ordinal);
        Assert.Contains("PayloadContratoJson", Fonte("ContratosAcmPostgreSqlReadRepository.cs"), StringComparison.Ordinal);
    }

    // ---------- 14) workers e Guardian inativos ----------

    [Fact]
    public void O_worker_de_integracao_so_e_registrado_quando_explicitamente_habilitado()
    {
        var fonte = Fonte("HostIntegracaoComposition.cs");
        var flag = fonte.IndexOf("FUGATES_INTEGRATION_WORKER_ENABLED", StringComparison.Ordinal);
        var hosted = fonte.IndexOf("AddHostedService<HospedagemIntegracaoWorker>", StringComparison.Ordinal);
        Assert.True(flag > 0 && hosted > flag, "o hosted service deve estar atras do flag explicito");
    }

    [Fact]
    public void O_pacote_do_receptor_dev_nasce_fechado_e_sem_janela_ativa()
    {
        var pacote = Path.Combine(RaizRepositorio(), "deploy", "sap-inbound-dev", "appsettings.receiver.dev.json");
        Assert.True(File.Exists(pacote), pacote);
        var texto = File.ReadAllText(pacote);
        var config = new ConfigurationBuilder().AddJsonFile(pacote).Build();

        // Destino unico e obrigatorio: fugates_dev:5433.
        var conexao = config["ConnectionStrings:FugaTES"]!;
        Assert.Contains("Port=5433", conexao, StringComparison.Ordinal);
        Assert.Contains("Database=fugates_dev", conexao, StringComparison.Ordinal);
        // R160: o receptor usa a identidade EXCLUSIVA de escrita, nunca a role leitora que serve
        // a API privada 18762. As duas permanecem separadas por desenho.
        Assert.Contains("Username=" + FugaTES.Persistencia.MotorIntegracao.NpgsqlDataSourceFactory.SapInboundApplicationRole,
            conexao, StringComparison.Ordinal);
        Assert.DoesNotContain(FugaTES.Persistencia.MotorIntegracao.NpgsqlDataSourceFactory.AcmApplicationRole,
            conexao, StringComparison.Ordinal);
        foreach (var proibida in new[] { "Port=5432", "Port=5434", "Port=15432", "Port=15433" })
            Assert.DoesNotContain(proibida, conexao, StringComparison.Ordinal);

        // Guards e escopo preservados; nenhuma janela real iniciada.
        Assert.Equal("LoopbackBasicHttps", config["SapContractInbound:SecurityMode"]);
        Assert.False(bool.Parse(config["MotorIntegracao:AcmInboundWindow:Enabled"]!));
        Assert.False(bool.Parse(config["FUGATES_INTEGRATION_WORKER_ENABLED"]!));
        Assert.False(bool.Parse(config["AcmR57:Normal:ExecutionAuthorized"]!));
        Assert.False(bool.Parse(config["Callback:Enabled"]!));
        Assert.Equal("3011", config["MotorIntegracao:AcmInboundWindow:Centro"]);

        // Fail-closed: pendencias explicitas impedem partida acidental.
        Assert.Contains("<<PENDENTE", texto, StringComparison.Ordinal);
        // A instancia isolada nao reusa as portas da API de leitura (18762) nem do servico de
        // comandos (18763). A verificacao e sobre o BINDING efetivo: os comentarios do pacote
        // citam essas portas justamente para registrar que elas nao sao tocadas.
        var urls = config["Urls"]!;
        foreach (var reservada in new[] { "18762", "18763", "18085", "18761", "8080", "5432", "5433", "5434" })
            Assert.DoesNotContain(reservada, urls, StringComparison.Ordinal);
        // R166: a porta deixou de ser pendencia (escolhida por preflight), mas continua restrita a
        // loopback e fora de todo servico existente.
        Assert.StartsWith("http://127.0.0.1:", urls, StringComparison.Ordinal);

        // R166: a credencial Basic NAO mora neste arquivo, nem como placeholder: entra pelo
        // ambiente do processo. Ausente, a composicao recusa com SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED.
        Assert.Null(config["SapInbound:BasicAuth:Username"]);
        Assert.Null(config["SapInbound:BasicAuth:Password"]);
        Assert.DoesNotContain("BasicAuth\":", texto, StringComparison.Ordinal);
        // Nenhum segredo em texto claro.
        foreach (var segredo in new[] { "\"senha\"", "BEGIN RSA", "Password=" })
            Assert.DoesNotContain(segredo, texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void O_perfil_pendente_do_receptor_nao_sobe_a_composicao_produtiva()
    {
        var pacote = Path.Combine(RaizRepositorio(), "deploy", "sap-inbound-dev", "appsettings.receiver.dev.json");
        var config = new ConfigurationBuilder().AddJsonFile(pacote).Build();
        // Como entregue, falta a credencial Basic (que vem do ambiente) E a janela R57 esta
        // fechada. A composicao recusa na PRIMEIRA barreira: a credencial.
        var erro = Assert.Throws<InvalidOperationException>(() =>
            SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(), config));
        Assert.Equal("SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED", erro.Message);

        // E, fornecida a credencial, a admissao R57 continua obrigatoria: a segunda barreira
        // permanece de pe, nao foi substituida pela primeira.
        var comBasic = new ConfigurationBuilder().AddJsonFile(pacote).AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["SapInbound:BasicAuth:Username"] = "sintetico",
                ["SapInbound:BasicAuth:Password"] = "sintetico"
            }).Build();
        var erroR57 = Assert.Throws<InvalidOperationException>(() =>
            SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(), comBasic));
        Assert.Equal("SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED", erroR57.Message);
    }

    // ---------- fonte canonica do Desktop unificado ----------

    [Fact]
    public void As_composicoes_legadas_nao_abrem_o_cluster_5432_no_perfil_unificado()
    {
        foreach (var nome in new[] { "ContratosAcmComposition.cs", "ProtocolosPreCadastroComposition.cs", "MonitorComposition.cs" })
        {
            var fonte = Fonte(nome);
            Assert.Contains("UnifiedProfileGuard.RuntimeLegadoPermitido", fonte, StringComparison.Ordinal);
            Assert.DoesNotContain("DesktopRuntime.Current is { } runtime", fonte, StringComparison.Ordinal);
        }
        // A guarda nao altera a configuracao do sistema legado.
        Assert.Contains("FugaTES.unified.json", Fonte("UnifiedProfileGuard.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mensagem_de_desconexao_mostra_o_endpoint_efetivo_do_perfil()
    {
        var fonte = Fonte("PhysicalReadConnection.cs");
        // A porta local do tunel e 18761 por construcao; a remota vem do perfil.
        Assert.Contains("LocalReadPort=18761", fonte, StringComparison.Ordinal);
        Assert.Contains("RemoteReadPort", fonte, StringComparison.Ordinal);
        Assert.Contains("porta remota", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("API privada localhost:18761", fonte, StringComparison.Ordinal);
    }

    // ---------- configuracao de apoio ----------

    private static AcmInboundWindowOptions Janela() => new()
    {
        Enabled = true,
        WindowId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
        AuthorizationReference = "FUGATES-R158-TESTE-OFFLINE",
        NotBeforeUtc = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero),
        UntilUtc = new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero),
        Centro = "3011",
        Item = "10",
        AllowedSapPartners = ["0000500123"],
        AllowedSapMaterials = ["000000000000100045"],
        ExcludedContractNumbers = []
    };

    private static IConfiguration Config(Action<Dictionary<string, string?>>? ajuste = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["SapContractInbound:ProductiveEnabled"] = "true",
            ["SapContractInbound:AutomaticProcessingEnabled"] = "true",
            ["SapContractInbound:SecurityMode"] = "LoopbackBasicHttps",
            ["SapInbound:BasicAuth:Username"] = "offline-user",
            ["SapInbound:BasicAuth:Password"] = "offline-reference",
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/home/michael/.fugates-acm-r165p/pgpass",
            ["MotorIntegracao:LeaseDuration"] = "00:03:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "1.00:00:00",
            ["HostIntegracao:PersistenceMode"] = "PostgreSql",
            ["HostIntegracao:ExternalMode"] = "Guardian",
            ["AcmR57:Enabled"] = "true",
            ["AcmR57:Normal:Enabled"] = "true"
        };
        ajuste?.Invoke(d);
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }
}
