using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Integracao.Toledo.Set26;

namespace FugaTES.Testes;

public sealed class Inc006RecebimentoMvpSet26Tests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void Emissor_set26_serializa_tipo_documento_canonico(string tipoDocumento)
    {
        var json = JsonNode.Parse(MapeadorGuardianSet26.SerializarEmissor(
            new EmissorGuardianSet26("BP-1", "Emissor", "Emissor SA", "1", tipoDocumento)))!.AsObject();

        Assert.Equal(tipoDocumento, (string?)json["tipoDocumento"]);
        Assert.Equal("1", (string?)json["estado"]);
        Assert.False(json.ContainsKey("tipoEmissor"));
        Assert.DoesNotContain("303404", json.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("303405", json.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Transportadora_set26_materializa_contrato_minimo()
    {
        var json = JsonNode.Parse(MapeadorGuardianSet26.SerializarTransportadora(
            new TransportadoraGuardianSet26("TR-1", "Transportadora", "Transportadora SA")))!.AsObject();

        Assert.Equal("TR-1", (string?)json["codigo"]);
        Assert.Equal("Transportadora", (string?)json["descricao"]);
        Assert.Equal("Transportadora SA", (string?)json["razaoSocial"]);
        Assert.Equal(3, json.Count);
    }

    [Fact]
    public void Sap_input_valido_mapeia_pre_cadastro_set26_sem_legados()
    {
        var preCadastro = MapeadorGuardianSet26.MapearPreCadastro(Input(), Opcoes());
        var json = MapeadorGuardianSet26.SerializarPreCadastro(preCadastro);

        Assert.Equal(0, preCadastro.Id);
        Assert.Equal("FLX_RECEBIMENTO", preCadastro.CodigoFluxo);
        Assert.Equal("MAT-001", preCadastro.CodigoItem);
        Assert.Equal("BP-EMISSOR", preCadastro.CodigoEmissor);
        Assert.Equal("BP-TRANSPORTADORA", preCadastro.CodigoTransportadora);
        Assert.Equal("CONTRATO-001", preCadastro.CodigoContrato);
        Assert.Equal(1, preCadastro.IdAgenda);
        Assert.Equal("DOC-001", preCadastro.NumeroReferencia);
        Assert.Equal([1000, 1002, 1003], preCadastro.CamposAdicionais.Select(x => x.Numero));
        Assert.DoesNotContain("CONTRATO-001C", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTRATO-001D", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Interno", json, StringComparison.Ordinal);
        var campos = JsonNode.Parse(json)!["dadosProtocoloPreCadastro"]!["camposAdicionais"]!.AsArray();
        Assert.DoesNotContain(campos, campo => (int?)campo!["numero"] == 1);
    }

    [Fact]
    public async Task Recebimento_valido_envia_pre_cadastro_e_correlaciona_documento_com_protocolo()
    {
        var guardian = new PreCadastroGuardianSet26GatewayFake("PROTOCOLO-001");
        var correlacoes = new RepositorioCorrelacaoRecebimentoMemoria();
        var sut = new ProcessarRecebimentoMvpCasoDeUso(guardian, correlacoes, Opcoes());

        var resultado = await sut.ExecutarAsync(Input(), CancellationToken.None);

        Assert.Equal("PROTOCOLO-001", resultado.ProtocoloGuardian);
        Assert.Equal("DOC-001", (await correlacoes.ObterPorProtocoloAsync("PROTOCOLO-001", CancellationToken.None))!.DocumentoSap);
        Assert.Equal(1, guardian.QuantidadeChamadas);
    }

    [Fact]
    public async Task Campo_obrigatorio_ausente_rejeita_antes_do_gateway()
    {
        var guardian = new PreCadastroGuardianSet26GatewayFake("PROTOCOLO-001");
        var sut = new ProcessarRecebimentoMvpCasoDeUso(guardian, new RepositorioCorrelacaoRecebimentoMemoria(), Opcoes());
        var invalido = Input() with { Material = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => sut.ExecutarAsync(invalido, CancellationToken.None));
        Assert.Equal(0, guardian.QuantidadeChamadas);
    }

    [Fact]
    public async Task Callback_captura_ticket_pesos_correlacao_e_entrega_pesagem_sap()
    {
        var (_, callback, sap, correlacoes) = await CriarFluxoAsync();

        var resultado = await callback.ExecutarAsync(Callback(), CancellationToken.None);

        Assert.Equal(StatusCallbackGuardianMvp.Processado, resultado.Status);
        Assert.Equal("TICKET-001", resultado.Pesagem!.IdPesagem);
        Assert.Equal(1200m, resultado.Pesagem.PesoBruto);
        Assert.Equal(200m, resultado.Pesagem.PesoTara);
        Assert.Equal(1000m, resultado.Pesagem.PesoAntesDesconto);
        Assert.Equal("DOC-001", resultado.Pesagem.DocumentoSap);
        Assert.Equal("TICKET-001", (await correlacoes.ObterPorProtocoloAsync("PROTOCOLO-001", CancellationToken.None))!.TicketGuardian);
        Assert.Equal(1, sap.QuantidadeChamadas);
    }

    [Fact]
    public async Task Callback_duplicado_equivalente_e_idempotente_sem_reenvio_sap()
    {
        var (_, callback, sap, _) = await CriarFluxoAsync();
        await callback.ExecutarAsync(Callback(), CancellationToken.None);

        var repetido = await callback.ExecutarAsync(Callback(), CancellationToken.None);

        Assert.Equal(StatusCallbackGuardianMvp.Idempotente, repetido.Status);
        Assert.Equal(1, sap.QuantidadeChamadas);
    }

    [Fact]
    public async Task Callback_duplicado_conflitante_falha_fechado()
    {
        var (_, callback, sap, _) = await CriarFluxoAsync();
        await callback.ExecutarAsync(Callback(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => callback.ExecutarAsync(Callback() with { PesoBruto = 1300m }, CancellationToken.None));
        Assert.Equal(1, sap.QuantidadeChamadas);
    }

    private static async Task<(ProcessarRecebimentoMvpCasoDeUso Recebimento, ProcessarCallbackGuardianMvpCasoDeUso Callback, PesagemSapMvpGatewayFake Sap, RepositorioCorrelacaoRecebimentoMemoria Correlacoes)> CriarFluxoAsync()
    {
        var correlacoes = new RepositorioCorrelacaoRecebimentoMemoria();
        var recebimento = new ProcessarRecebimentoMvpCasoDeUso(new PreCadastroGuardianSet26GatewayFake("PROTOCOLO-001"), correlacoes, Opcoes());
        var sap = new PesagemSapMvpGatewayFake();
        var callback = new ProcessarCallbackGuardianMvpCasoDeUso(correlacoes, sap);
        await recebimento.ExecutarAsync(Input(), CancellationToken.None);
        return (recebimento, callback, sap, correlacoes);
    }

    private static RecebimentoSapCpiMvp Input() => new(
        "RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-001", "BP-EMISSOR", "BP-TRANSPORTADORA",
        "CONTRATO-001", "DOC-001", "COMPRA", "CENTRO-001", "ENTRADA", "NF", "PO-001", 7, "ACM-001");

    private static GuardianSet26Options Opcoes() => new("https://guardian.invalid/pre-cadastro", "https://fugates.invalid/callback");

    private static CallbackGuardianMvp Callback() => new("PROTOCOLO-001", "TICKET-001", 1200m, 200m, 1000m, "CONCLUIDO");
}
