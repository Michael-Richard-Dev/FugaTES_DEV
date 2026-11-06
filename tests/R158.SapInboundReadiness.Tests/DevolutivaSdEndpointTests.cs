using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace R158.SapInboundReadiness.Tests;

// SD: o endpoint que recebe a pesagem do Guardian. Nenhum banco: o repositorio e de mentira,
// para o que se prova aqui ser o comportamento HTTP e a decisao sobre reenvio.
public sealed class DevolutivaSdEndpointTests
{
    private sealed class RepositorioFalso : IDevolutivasSdRepositorio
    {
        public List<DevolutivaPesagemSd> Gravadas { get; } = [];
        public HashSet<string> JaRecebidas { get; } = [];

        public Task<IReadOnlyList<DevolutivaSdLinha>> ListarAsync(int limite, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DevolutivaSdLinha>>([]);

        public Task RegistrarAsync(DevolutivaPesagemSd devolutiva, CancellationToken ct)
        {
            if (!JaRecebidas.Add(devolutiva.IdPesagem))
                throw new DevolutivaSdJaRecebidaException(devolutiva.IdPesagem);
            Gravadas.Add(devolutiva);
            return Task.CompletedTask;
        }
    }

    // Chama o manipulador direto, sem servidor: o que se prova aqui e a decisao sobre o corpo e
    // sobre o reenvio, nao o roteamento do ASP.NET.
    private static async Task<(int Status, JsonElement Corpo)> Enviar(
        IDevolutivasSdRepositorio? repositorio, string corpo, string tipoConteudo = "application/json")
    {
        var servicos = new ServiceCollection();
        if (repositorio is not null) servicos.AddSingleton(repositorio);
        var contexto = new DefaultHttpContext { RequestServices = servicos.BuildServiceProvider() };
        contexto.Request.Method = "POST";
        contexto.Request.ContentType = tipoConteudo;
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

    private static Task<(int Status, JsonElement Corpo)> Postar(
        string corpo, string tipoConteudo = "application/json", bool comRepositorio = true,
        RepositorioFalso? repositorio = null)
        => Enviar(comRepositorio ? repositorio ?? new RepositorioFalso() : null, corpo, tipoConteudo);

    private const string Venda = """
    {"SistemaBalanca":"Toledo","DataPesagem":"10/31/2026 11:40:18 AM","TipoPesagem":"S",
     "IdPesagem":"0020327","Centro":"3011","PesoBruto":"20000.0","PesoTara":"10000.0",
     "PesoAposDesconto":"9870.5","Placa":"COF7595","NomeMotorista":"MARCIO FERENZ",
     "camposAdicionais":[{"numero":1003,"valor":"0000000782"},{"numero":1009,"valor":"000010"}]}
    """;

    // Mesmo documento, sem os campos que apontam a ordem.
    private const string VendaSemCampos = """
    {"SistemaBalanca":"Toledo","DataPesagem":"10/31/2026 11:40:18 AM","TipoPesagem":"S",
     "IdPesagem":"0020328","Centro":"3011","PesoBruto":"20000.0","PesoTara":"10000.0",
     "PesoAposDesconto":"9870.5","Placa":"COF7595","NomeMotorista":"MARCIO FERENZ"}
    """;

    [Fact]
    public async Task Pesagem_de_venda_e_recebida_e_correlacionada()
    {
        var repositorio = new RepositorioFalso();
        var (status, corpo) = await Postar(Venda, repositorio: repositorio);
        Assert.Equal(200, status);
        Assert.Equal("RECEBIDA", corpo.GetProperty("status").GetString());
        Assert.True(corpo.GetProperty("persisted").GetBoolean());
        Assert.Equal("CORRELACIONADA", corpo.GetProperty("correlacao").GetString());
        Assert.Equal("782/10", corpo.GetProperty("chave").GetString());
        Assert.Equal("782/10", Assert.Single(repositorio.Gravadas).ChaveNatural);
    }

    // Reenvio nao e erro: o Guardian repete quando nao tem certeza de que chegou. Responder 200
    // encerra a repeticao; um 4xx faria parecer falha e ele insistiria.
    [Fact]
    public async Task Reenvio_da_mesma_pesagem_responde_ok_sem_gravar_de_novo()
    {
        var repositorio = new RepositorioFalso();

        var primeira = await Postar(Venda, repositorio: repositorio);
        Assert.Equal(200, primeira.Status);
        Assert.Equal("RECEBIDA", primeira.Corpo.GetProperty("status").GetString());

        var segunda = await Postar(Venda, repositorio: repositorio);
        Assert.Equal(200, segunda.Status);
        Assert.Equal("JA_RECEBIDA", segunda.Corpo.GetProperty("status").GetString());
        Assert.False(segunda.Corpo.GetProperty("persisted").GetBoolean());
        Assert.Single(repositorio.Gravadas);
    }

    // Sem os campos adicionais a pesagem entra assim mesmo, sinalizada: perder o documento seria
    // pior do que guarda-lo sem saber a qual ordem pertence.
    [Fact]
    public async Task Pesagem_sem_correlacao_e_recebida_e_sinalizada()
    {
        var repositorio = new RepositorioFalso();
        var (status, corpo) = await Postar(VendaSemCampos, repositorio: repositorio);
        Assert.Equal(200, status);
        Assert.Equal("SEM_CORRELACAO", corpo.GetProperty("correlacao").GetString());
        Assert.True(corpo.GetProperty("persisted").GetBoolean());
        Assert.False(Assert.Single(repositorio.Gravadas).TemCorrelacao);
    }

    /// <summary>
    /// Recebimento tem rota propria e regras proprias; aceitar aqui faria uma entrada virar
    /// venda, entao NADA e gravado. Mas tambem nao e recusa: o Guardian manda a mesma
    /// finalizacao para as duas rotas, e o documento esta certo - so pertence ao outro fluxo.
    ///
    /// Ate 06/11/2026 isto respondia 400, e com "Valor em Caso de Sucesso = OK" configurado no
    /// Guardian o 400 fazia ele marcar falha e REENVIAR PARA SEMPRE uma pesagem que esta certa.
    /// E o mesmo tratamento que o contrato ACM ja recebia; o que faltava era o tipo chegar la.
    /// </summary>
    [Fact]
    public async Task Pesagem_de_entrada_nao_e_gravada_e_nao_manda_o_guardian_reenviar()
    {
        var repositorio = new RepositorioFalso();
        var (status, corpo) = await Postar(Venda.Replace("\"TipoPesagem\":\"S\"", "\"TipoPesagem\":\"E\"", StringComparison.Ordinal), repositorio: repositorio);

        Assert.Equal(200, status);
        // "mensagem": OK e o unico campo que o Guardian le para decidir sucesso.
        Assert.Equal("OK", corpo.GetProperty("mensagem").GetString());
        Assert.Equal("NAO_E_DO_SD", corpo.GetProperty("status").GetString());
        Assert.Equal("0020327", corpo.GetProperty("idPesagem").GetString());
        Assert.False(corpo.GetProperty("persisted").GetBoolean());
        Assert.Empty(repositorio.Gravadas);
    }

    /// <summary>
    /// A distincao que importa: "nao e do SD" responde 200, mas documento ERRADO continua
    /// respondendo 400 com a causa. Trocar os dois faria o Guardian desistir de reenviar uma
    /// pesagem que ele deveria reenviar depois de corrigida.
    /// </summary>
    [Fact]
    public async Task Documento_errado_continua_sendo_recusado_mesmo_sendo_de_saida()
    {
        var (status, corpo) = await Postar(Venda.Replace("\"TipoPesagem\":\"S\"", "\"TipoPesagem\":\"\"", StringComparison.Ordinal));

        Assert.Equal(400, status);
        Assert.Equal("SD_DEVOLUTIVA_CAMPO_AUSENTE", corpo.GetProperty("codigo").GetString());
    }

    /// <summary>
    /// O Enter cru na observacao - parte do processo, nao acidente: e o que separa o lacre da
    /// carga do lacre do envelope da amostra testemunha. Ate 06/11/2026 isto dava 400
    /// "SD_DEVOLUTIVA_JSON_INVALIDO" e a pesagem 0030384 ficou duas vezes de fora.
    /// </summary>
    [Fact]
    public async Task Observacao_com_enter_cru_e_aceita_e_gravada()
    {
        var repositorio = new RepositorioFalso();
        var comEnter = Venda.Replace(
            "\"Placa\":\"COF7595\"",
            "\"Placa\":\"COF7595\",\"Observacao\":\"148594,148595 \r\n59187,59188\"",
            StringComparison.Ordinal);

        var (status, corpo) = await Postar(comEnter, repositorio: repositorio);

        Assert.Equal(200, status);
        Assert.Equal("OK", corpo.GetProperty("mensagem").GetString());
        Assert.True(corpo.GetProperty("persisted").GetBoolean());
        Assert.Single(repositorio.Gravadas);
    }

    [Fact]
    public async Task Corpo_fora_do_contrato_e_recusado_com_motivo()
    {
        var repositorio = new RepositorioFalso();
        var (status, corpo) = await Postar("{\"nada\":1}", repositorio: repositorio);
        Assert.Equal(400, status);
        Assert.Equal("SD_DEVOLUTIVA_CAMPO_AUSENTE", corpo.GetProperty("codigo").GetString());
        Assert.False(corpo.GetProperty("persisted").GetBoolean());
        Assert.Empty(repositorio.Gravadas);
    }

    [Fact]
    public async Task Tipo_de_conteudo_errado_e_recusado()
    {
        var (status, corpo) = await Postar(Venda, "text/plain");
        Assert.Equal(415, status);
        Assert.Equal("INVALID_CONTENT_TYPE", corpo.GetProperty("codigo").GetString());
    }

    // Sem receptor configurado o endpoint diz isso, em vez de aceitar e perder o documento.
    [Fact]
    public async Task Sem_repositorio_responde_indisponivel_e_nao_finge_ter_gravado()
    {
        var (status, corpo) = await Postar(Venda, comRepositorio: false);
        Assert.Equal(503, status);
        Assert.Equal("SD_RECEPTOR_NAO_CONFIGURADO", corpo.GetProperty("codigo").GetString());
        Assert.False(corpo.GetProperty("persisted").GetBoolean());
    }

    // A rota do SD e separada da do ACM de proposito.
    [Fact]
    public void Rota_do_sd_nao_colide_com_a_do_recebimento()
    {
        Assert.Equal("/api/guardian/set26/callback/venda", DevolutivaSdEndpoint.Route);
        Assert.NotEqual(ToledoCallbackProbeEndpoint.ReceiptRoute, DevolutivaSdEndpoint.Route);
    }

    // --- O contrato de resposta que o Guardian exige --------------------------------------
    //
    // A integracao do Guardian tem "Campo de Retorno do Metodo = mensagem" e "Valor em Caso de
    // Sucesso = OK". Sem o campo "mensagem", ele marca falha mesmo recebendo HTTP 200 e corpo
    // valido (erro 309087, "o campo configurado como result nao foi encontrado") e fica
    // reenviando. Conferido na tela de configuracao em 02/11/2026, com a pesagem 0020338.

    [Fact]
    public async Task Sucesso_devolve_mensagem_OK_para_o_guardian()
    {
        var (status, corpo) = await Postar(Venda, repositorio: new RepositorioFalso());
        Assert.Equal(200, status);
        Assert.Equal("OK", corpo.GetProperty("mensagem").GetString());
    }

    [Fact]
    public async Task Reenvio_tambem_devolve_mensagem_OK()
    {
        // Reenvio e sucesso para o Guardian: nao ha o que reenviar de novo.
        var repositorio = new RepositorioFalso();
        _ = await Postar(Venda, repositorio: repositorio);
        var segunda = await Postar(Venda, repositorio: repositorio);
        Assert.Equal("OK", segunda.Corpo.GetProperty("mensagem").GetString());
    }

    [Fact]
    public async Task Recusa_devolve_mensagem_diferente_de_OK_com_a_causa()
    {
        // Um documento de fato ERRADO - sem o centro. O tipo "E" servia de exemplo aqui ate
        // 06/11/2026; deixou de servir porque recebimento nao e recusa, e sim outro fluxo: ele
        // agora responde 200 (ver Pesagem_de_entrada_nao_e_gravada_e_nao_manda_o_guardian_reenviar).
        var semCentro = Venda.Replace("\"Centro\":\"3011\",", "", StringComparison.Ordinal);
        var (status, corpo) = await Postar(semCentro, repositorio: new RepositorioFalso());
        Assert.Equal(400, status);
        var mensagem = corpo.GetProperty("mensagem").GetString();
        Assert.NotEqual("OK", mensagem);
        // A causa vai no proprio campo que ele le, e nao so num campo que ele ignora.
        Assert.Equal(corpo.GetProperty("codigo").GetString(), mensagem);
    }
}
