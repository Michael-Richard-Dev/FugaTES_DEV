using System.Globalization;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Infraestrutura.ContratosSapToledo;
using FugaTES.Infraestrutura.MotorIntegracao;

namespace FugaTES.Testes;

public sealed class Inc006H4DurableContractTests
{
    [Fact]
    public void Canonicalizer_ProducesStableUppercaseSha256AndNormalizesSemanticContent()
    {
        var canonicalizer = new ContratoSapToledoCanonicalizer();
        var a = Snapshot("Caf\u00e9", ["3012", "3011"]);
        var b = Snapshot("Cafe\u0301", ["3011", "3012"]);

        var first = canonicalizer.Canonicalizar(a);
        var second = canonicalizer.Canonicalizar(b);

        Assert.Equal(first.FingerprintSha256, second.FingerprintSha256);
        Assert.Matches("^[0-9A-F]{64}$", first.FingerprintSha256);
        Assert.Equal(["3011", "3012"], first.Snapshot.CentroOpcional);
        Assert.Throws<ArgumentException>(() => canonicalizer.Canonicalizar(Snapshot("x", ["3011", "3011"])));
        Assert.NotEqual(
            canonicalizer.Canonicalizar(Snapshot(null, [])).FingerprintSha256,
            canonicalizer.Canonicalizar(Snapshot(string.Empty, [])).FingerprintSha256);
    }

    [Fact]
    public void Canonicalizer_IsIndependentFromCurrentCulture()
    {
        var canonicalizer = new ContratoSapToledoCanonicalizer();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var pt = canonicalizer.Canonicalizar(Snapshot("x", [])).FingerprintSha256;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(pt, canonicalizer.Canonicalizar(Snapshot("x", [])).FingerprintSha256);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task FakeStore_DeduplicatesAndAllocatesMonotonicImmutableVersions()
    {
        var motor = new RepositorioMotorIntegracaoFake();
        IContratoSapToledoVersaoStore store = new ContratoSapToledoVersaoStoreFake(motor);
        var canonicalizer = new ContratoSapToledoCanonicalizer();
        var a = canonicalizer.Canonicalizar(Snapshot("A", []));
        var b = canonicalizer.Canonicalizar(Snapshot("B", []));

        var first = await store.RegistrarOuObterAsync(a.Snapshot, a.FingerprintSha256, a.PayloadCanonicoJson, "corr-1", CancellationToken.None);
        var replay = await store.RegistrarOuObterAsync(a.Snapshot, a.FingerprintSha256, a.PayloadCanonicoJson, "corr-2", CancellationToken.None);
        var second = await store.RegistrarOuObterAsync(b.Snapshot, b.FingerprintSha256, b.PayloadCanonicoJson, "corr-3", CancellationToken.None);

        Assert.Equal(ResultadoRegistroVersaoContrato.Criada, first.Resultado);
        Assert.Equal(ResultadoRegistroVersaoContrato.JaExistente, replay.Resultado);
        Assert.Equal(first.IntegracaoId, replay.IntegracaoId);
        Assert.Equal(1, first.Versao);
        Assert.Equal(2, second.Versao);
        Assert.Equal(a.Snapshot, (await store.ObterPorIntegracaoIdAsync(first.IntegracaoId, CancellationToken.None))!.Snapshot);
    }

    private static ContratoSapToledoSnapshot Snapshot(string? observacao, IReadOnlyCollection<string?> centros) =>
        new("Toledo", "0000001008", "AC3P", "Descricao", "11/09/2026", "11/09/2026", "31/12/2026",
            10, "31/12/2026", "T", 100000m, 10000m, "0020000051", "000000000007000001", "E",
            "0000000042", "001", "001", observacao, 1.5m, "3011", "10", centros);
}
