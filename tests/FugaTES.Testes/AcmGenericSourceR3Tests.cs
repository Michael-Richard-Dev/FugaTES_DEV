using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Testes;

public sealed class AcmGenericSourceR3Tests
{
    [Fact]
    public void Missing_generic_proximity_is_blocked_even_for_previous_fixture()
    {
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.ResolverValorProximidadeFim(AcmGuardianReferenceMapperTests.VersaoAprovada()));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", error.Decisao);
    }

    [Fact]
    public void Production_has_no_contract_fixture_specialization()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName,"FugaTES.slnx"))) dir=dir.Parent;
        Assert.NotNull(dir);
        var mappingRoot=Path.Combine(dir!.FullName,"src","FugaTES.Aplicacao","Servicos","ContratosSapToledo");
        var mvpRoot=Path.Combine(dir.FullName,"src","FugaTES.Aplicacao","Servicos","Mvp");
        foreach (var file in Directory.EnumerateFiles(mappingRoot,"*.cs",SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(mvpRoot,"*.cs",SearchOption.AllDirectories))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar) &&
                        !p.Contains(Path.DirectorySeparatorChar+"bin"+Path.DirectorySeparatorChar)))
        {
            // Scoped read-only tools may reject historical IDs; generic mapping may not specialize them.
            var text=File.ReadAllText(file);
            Assert.DoesNotContain("0000001031",text);
            Assert.DoesNotContain("OC1031",text);
            Assert.DoesNotContain("0b3c570c-0714-4146-ac2d-95034c555658",text);
        }
    }
}
