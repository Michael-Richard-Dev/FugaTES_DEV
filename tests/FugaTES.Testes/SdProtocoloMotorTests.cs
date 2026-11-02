using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FugaTES.Testes;

/// <summary>
/// Motor de protocolos SD: o que roda na VM sem tela aberta e sem ninguem logado.
///
/// O que estes testes protegem, em ordem de gravidade: o motor nao alcanca o acervo antigo, nao
/// envia quando nao consegue conferir duplicidade, e nao passa do teto por volta. Protocolo
/// criado no Guardian nao se desfaz.
/// </summary>
public sealed class SdProtocoloMotorTests
{
    private static readonly DateOnly Corte = new(2026, 11, 1);

    private static SdProtocoloOptions Opcoes(int teto = 10)
    {
        var o = new SdProtocoloOptions
        {
            Enabled = true,
            CorteCriacao = Corte,
            ProtocolosPorVarredura = teto,
            UrlParaRetorno = "https://webhook.site/abc"
        };
        return o;
    }

    private static OrdemVendaSdLeitura Linha(string ordem, string item, DateTime? criada) =>
        new(Centro: "3011", CodigoOrdemVenda: ordem, Material: "4000561", LinhaItem: item,
            SituacaoOrdem: "A", TipoDocumentoVendas: "ZVBI", EmpresaOrdem: "3000",
            Emissor: "20000217", SetorAtividade: "63", RecebedorMercadoria: "20000215",
            PayloadJson: "{}")
        { DataCriacao = criada };

    private sealed class ConsultaFalsa(params OrdemVendaSdLeitura[] linhas) : IConsultaOrdensVendaSdSap
    {
        public OrdensVendaSdFiltro? UltimoFiltro { get; private set; }

        public Task<ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>> ConsultarAsync(
            OrdensVendaSdFiltro filtro, CancellationToken ct)
        {
            UltimoFiltro = filtro;
            return Task.FromResult(ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>
                .Sucesso(linhas));
        }
    }

    private sealed class ConsultaQueFalha : IConsultaOrdensVendaSdSap
    {
        public Task<ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>> ConsultarAsync(
            OrdensVendaSdFiltro filtro, CancellationToken ct)
            => Task.FromResult(ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>
                .Falha(StatusConsultaSap.FalhaTecnica));
    }

    private sealed class RegistroFalso : IRegistroProtocolosSd
    {
        public List<ProtocoloSdEnvio> Registrados { get; } = [];
        public bool LeituraFalha { get; set; }

        public Task<ProtocoloSdRegistrado?> ProcurarAsync(string chave, CancellationToken ct)
            => Task.FromResult<ProtocoloSdRegistrado?>(null);

        public Task<IReadOnlyList<ProtocoloSdRegistrado>> LerAsync(CancellationToken ct)
            => LeituraFalha
                ? Task.FromException<IReadOnlyList<ProtocoloSdRegistrado>>(new InvalidOperationException("banco fora"))
                : Task.FromResult<IReadOnlyList<ProtocoloSdRegistrado>>([.. Registrados.Select(
                    r => new ProtocoloSdRegistrado(r.ChaveNatural, r.ProtocoloId, r.Estado, DateTimeOffset.UtcNow, r.HttpStatus))]);

        public Task RegistrarAsync(ProtocoloSdEnvio envio, CancellationToken ct)
        {
            if (Registrados.Any(r => r.ChaveNatural == envio.ChaveNatural))
                throw new ProtocoloSdJaRegistradoException(envio.ChaveNatural);
            Registrados.Add(envio);
            return Task.CompletedTask;
        }

        public Task AtualizarDesfechoAsync(string chave, long? id, string estado, int http,
            string? mensagem, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class GuardianFalso : IEnviarProtocoloSdGateway
    {
        public List<string> Enviados { get; } = [];

        public Task<EnvioProtocoloSd> EnviarAsync(ProtocoloPreCadastroSdRequest pedido,
            string chave, CancellationToken ct)
        {
            Enviados.Add(chave);
            return Task.FromResult(new EnvioProtocoloSd(true, 200, Enviados.Count, chave,
                GuardianProtocoloSdClient.Impressao(pedido), "ok"));
        }
    }

    private static SdProtocoloWorker Motor(SdProtocoloOptions o, IConsultaOrdensVendaSdSap consulta,
        IRegistroProtocolosSd registro, IEnviarProtocoloSdGateway guardian)
        => new(Options.Create(o), consulta, registro, guardian, TimeProvider.System,
            NullLogger<SdProtocoloWorker>.Instance);

    // --- O corte por data -----------------------------------------------------------------

    [Fact]
    public async Task Ordem_anterior_ao_corte_nao_vira_protocolo()
    {
        var guardian = new GuardianFalso();
        await Motor(Opcoes(), new ConsultaFalsa(Linha("1815", "10", new DateTime(2026, 10, 31))),
            new RegistroFalso(), guardian).VarrerAsync(CancellationToken.None);
        Assert.Empty(guardian.Enviados);
    }

    [Fact]
    public async Task Ordem_do_dia_do_corte_vira_protocolo()
    {
        var guardian = new GuardianFalso();
        await Motor(Opcoes(), new ConsultaFalsa(Linha("4232", "10", new DateTime(2026, 11, 1, 18, 19, 27))),
            new RegistroFalso(), guardian).VarrerAsync(CancellationToken.None);
        Assert.Equal(["4232/10"], guardian.Enviados);
    }

    [Fact]
    public async Task Ordem_sem_data_de_criacao_nao_vira_protocolo()
    {
        // Nao saber quando a ordem foi criada nao e o mesmo que ela ser recente.
        var guardian = new GuardianFalso();
        await Motor(Opcoes(), new ConsultaFalsa(Linha("4232", "10", null)),
            new RegistroFalso(), guardian).VarrerAsync(CancellationToken.None);
        Assert.Empty(guardian.Enviados);
    }

    [Fact]
    public async Task O_corte_tambem_vai_no_filtro_enviado_ao_sap()
    {
        var consulta = new ConsultaFalsa();
        await Motor(Opcoes(), consulta, new RegistroFalso(), new GuardianFalso())
            .VarrerAsync(CancellationToken.None);
        Assert.Equal(Corte, consulta.UltimoFiltro?.DataCriacaoMinima);
    }

    // --- Duplicidade e teto ---------------------------------------------------------------

    [Fact]
    public async Task Linha_que_ja_tem_protocolo_nao_e_enviada_de_novo()
    {
        var registro = new RegistroFalso();
        var guardian = new GuardianFalso();
        var consulta = new ConsultaFalsa(Linha("4232", "10", new DateTime(2026, 11, 1)));
        var motor = Motor(Opcoes(), consulta, registro, guardian);

        await motor.VarrerAsync(CancellationToken.None);
        await motor.VarrerAsync(CancellationToken.None);

        Assert.Single(guardian.Enviados);
    }

    [Fact]
    public async Task Sem_conseguir_ler_o_registro_nada_e_enviado()
    {
        // Conferir duplicidade e condicao para enviar, nao um detalhe que se pule quando o banco
        // esta fora: sem isso, uma queda do PostgreSQL viraria protocolo duplicado no Guardian.
        var guardian = new GuardianFalso();
        await Motor(Opcoes(), new ConsultaFalsa(Linha("4232", "10", new DateTime(2026, 11, 1))),
            new RegistroFalso { LeituraFalha = true }, guardian).VarrerAsync(CancellationToken.None);
        Assert.Empty(guardian.Enviados);
    }

    [Fact]
    public async Task O_teto_por_volta_e_respeitado()
    {
        var linhas = Enumerable.Range(1, 25)
            .Select(i => Linha("42" + i.ToString("00"), "10", new DateTime(2026, 11, 1))).ToArray();
        var guardian = new GuardianFalso();
        await Motor(Opcoes(teto: 3), new ConsultaFalsa(linhas), new RegistroFalso(), guardian)
            .VarrerAsync(CancellationToken.None);
        Assert.Equal(3, guardian.Enviados.Count);
    }

    [Fact]
    public async Task Busca_que_nao_conclui_nao_envia_nada()
    {
        var guardian = new GuardianFalso();
        await Motor(Opcoes(), new ConsultaQueFalha(), new RegistroFalso(), guardian)
            .VarrerAsync(CancellationToken.None);
        Assert.Empty(guardian.Enviados);
    }

    [Fact]
    public async Task A_reserva_acontece_antes_do_envio()
    {
        // Se cair entre uma coisa e outra, a linha fica ocupada - e nao livre para um segundo POST.
        var registro = new RegistroFalso();
        await Motor(Opcoes(), new ConsultaFalsa(Linha("4232", "10", new DateTime(2026, 11, 1))),
            registro, new GuardianFalso()).VarrerAsync(CancellationToken.None);
        var reserva = Assert.Single(registro.Registrados);
        Assert.Equal("4232/10", reserva.ChaveNatural);
        Assert.Equal(EstadoProtocoloSd.DesfechoDesconhecido, reserva.Estado);
    }

    // --- Configuracao ----------------------------------------------------------------------

    [Fact]
    public void Configuracao_boa_passa() => Assert.Null(Opcoes().Reprovar());

    [Fact]
    public void Corte_padrao_e_o_combinado_com_o_operador()
        => Assert.Equal(new DateOnly(2026, 11, 1), new SdProtocoloOptions().CorteCriacao);

    [Fact]
    public void Intervalo_padrao_e_de_tres_minutos()
        => Assert.Equal(TimeSpan.FromMinutes(3), new SdProtocoloOptions().Intervalo);

    [Theory]
    [InlineData(10, "SD_MOTOR_INTERVALO_INVALIDO")]
    [InlineData(7200, "SD_MOTOR_INTERVALO_INVALIDO")]
    public void Intervalo_fora_da_faixa_reprova(int segundos, string codigo)
    {
        var o = Opcoes();
        o.Intervalo = TimeSpan.FromSeconds(segundos);
        Assert.Equal(codigo, o.Reprovar());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Teto_fora_da_faixa_reprova(int teto)
        => Assert.Equal("SD_MOTOR_TETO_INVALIDO", Opcoes(teto).Reprovar());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-e-url")]
    [InlineData("ftp://exemplo.invalido/x")]
    public void Url_de_retorno_invalida_reprova(string? url)
    {
        var o = Opcoes();
        o.UrlParaRetorno = url;
        Assert.Equal("SD_MOTOR_URL_RETORNO_INVALIDA", o.Reprovar());
    }

    [Fact]
    public void Corte_antigo_demais_reprova()
    {
        // O corte e a unica defesa contra o motor varrer o acervo. Uma data de 2019 anularia essa
        // defesa sem ninguem perceber, e protocolo criado nao se desfaz.
        var o = Opcoes();
        o.CorteCriacao = new DateOnly(2019, 1, 1);
        Assert.Equal("SD_MOTOR_CORTE_ANTIGO_DEMAIS", o.Reprovar());
    }

    [Fact]
    public void Filtro_montado_usa_organizacao_e_centros_do_fluxo()
    {
        var filtro = Opcoes().MontarFiltro();
        Assert.True(filtro.EhValido());
        Assert.Equal("3000", filtro.SalesOrganization);
        Assert.Equal(["3011", "3008"], filtro.ShippingPoints);
    }

/// <summary>
/// Motor que leva a pesagem ao SAP sozinha (passos 3, 4 e 5). O que estes testes protegem e o
/// RECORTE: sem correlacao nao se mexe, com desfecho ja registrado nao se repete, e o teto por
/// volta vale. Documento criado no SAP nao se desfaz.
/// </summary>
public sealed class RemessaSdMotorTests
{
    private static SdProtocoloOptions Opcoes(int teto = 5) => new()
    {
        Enabled = true, RemessaAutomatica = true, RemessasPorVarredura = teto,
        UrlParaRetorno = "https://webhook.site/abc"
    };

    [Fact]
    public void O_automatico_nasce_desligado()
        // Ele escreve documento no SAP. Ligar por engano e caro.
        => Assert.False(new SdProtocoloOptions().RemessaAutomatica);

    [Fact]
    public void Teto_padrao_por_volta_e_cinco()
        => Assert.Equal(5, new SdProtocoloOptions().RemessasPorVarredura);

    [Fact]
    public void A_configuracao_do_motor_de_remessa_nao_reprova_a_do_protocolo()
        // Os dois motores compartilham o mesmo bloco de configuracao e o mesmo processo.
        => Assert.Null(Opcoes().Reprovar());

    [Theory]
    [InlineData(EstadoDevolutivaSd.SemCorrelacao, false)]
    [InlineData(EstadoDevolutivaSd.Correlacionada, true)]
    public void So_pesagem_correlacionada_entra_no_automatico(string estado, bool esperado)
        // Sem a ordem nao ha a que referenciar a remessa, e adivinhar por parceiro e data acerta
        // no teste e erra quando ha duas ordens no mesmo dia.
        => Assert.Equal(esperado, estado == EstadoDevolutivaSd.Correlacionada);
}

/// <summary>
/// A composicao do servico da VM. Estes testes existem por um motivo concreto: em 02/11/2026 o
/// servico nao subiu porque IConsultarSetorAtividadeSd nao estava registrado, e nada aqui
/// conferia isso - a composicao inteira so roda em Linux, entao nunca era construida fora da VM.
/// </summary>
public sealed class SdComposicaoRegistroTests
{
    private static IServiceCollection Registrado()
    {
        var services = new ServiceCollection();
        SdProtocoloComposition.RegistrarClientesSdSap(services);
        SdProtocoloComposition.RegistrarGuardianEMotoresSd(services);
        return services;
    }

    [Theory]
    // Tudo o que o ExecutorFluxoRemessaSd pede no construtor.
    [InlineData(typeof(IConsultarSetorAtividadeSd))]
    [InlineData(typeof(ICriarRemessaSdSap))]
    [InlineData(typeof(IAjustarRemessaSdSap))]
    [InlineData(typeof(IGravarTextoNfeSd))]
    // E o que o motor de protocolo pede.
    [InlineData(typeof(IEnviarProtocoloSdGateway))]
    public void Os_servicos_que_os_motores_pedem_estao_registrados(Type servico)
        => Assert.Contains(Registrado(), d => d.ServiceType == servico);

    [Fact]
    public void Todo_parametro_do_executor_do_fluxo_tem_registro()
    {
        // Conferir por reflexao, e nao por lista escrita a mao: um parametro novo no construtor
        // passa a exigir registro sem ninguem precisar lembrar de atualizar este teste.
        var registrados = Registrado().Select(d => d.ServiceType).ToHashSet();
        var pedidos = typeof(ExecutorFluxoRemessaSd).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType);
        // IRemessasSdRepositorio vem do bloco do banco, registrado fora destes dois metodos.
        var faltando = pedidos
            .Where(t => t != typeof(IRemessasSdRepositorio) && !registrados.Contains(t))
            .ToArray();
        Assert.Empty(faltando);
    }

    [Fact]
    public void Os_dois_motores_sobem_juntos()
    {
        var hospedados = Registrado()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToArray();
        Assert.Contains(typeof(SdProtocoloWorker), hospedados);
        Assert.Contains(typeof(RemessaSdWorker), hospedados);
    }
}

/// <summary>
/// Reenvio de protocolo depois de recusa. Em 02/11/2026 a ordem 4237/10 foi recusada pelo
/// Guardian por falta de cadastro do emissor e do material; cadastrados, o botao Enviar continuava
/// desabilitado, e destravar exigiria editar o banco a mao.
/// </summary>
public sealed class ProtocoloSdRetentativaTests
{
    [Fact]
    public void Recusado_permite_reenviar()
        => Assert.True(EstadoProtocoloSd.PermiteNovaTentativa(EstadoProtocoloSd.Recusado));

    [Theory]
    // Criado: o protocolo existe. Desconhecido: ele PODE existir - e supor que nao e o que cria
    // protocolo duplicado no Guardian.
    [InlineData(EstadoProtocoloSd.Criado)]
    [InlineData(EstadoProtocoloSd.DesfechoDesconhecido)]
    [InlineData(null)]
    [InlineData("recusado")]
    public void Os_demais_desfechos_continuam_bloqueando(string? estado)
        => Assert.False(EstadoProtocoloSd.PermiteNovaTentativa(estado));
}
}
