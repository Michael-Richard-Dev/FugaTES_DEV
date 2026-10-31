using System.Xml.Linq;
using FugaTES.HostIntegracao;
using FugaTES.WinForms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FugaTES.Testes;

public sealed class ArquiteturaTests
{
    [Fact]
    public void TA01_Dominio_NaoReferencia_QualquerOutroProjetoFugaTES()
    {
        var grafo = CarregarGrafoProjetos();

        Assert.Empty(grafo["FugaTES.Dominio"]);
    }

    [Fact]
    public void TA02_Aplicacao_NaoReferencia_CamadasOuHostsProibidos()
    {
        var referencias = CarregarGrafoProjetos()["FugaTES.Aplicacao"];

        Assert.Contains("FugaTES.Dominio", referencias);
        Assert.DoesNotContain("FugaTES.Persistencia", referencias);
        Assert.DoesNotContain("FugaTES.Integracao.SAP", referencias);
        Assert.DoesNotContain("FugaTES.Integracao.Toledo", referencias);
        Assert.DoesNotContain("FugaTES.WinForms", referencias);
        Assert.DoesNotContain("FugaTES.HostIntegracao", referencias);
        Assert.DoesNotContain("Npgsql", ObterPacotes("FugaTES.Aplicacao"));
    }

    [Fact]
    public void TA03_WinForms_NaoReferencia_IntegracaoSap()
    {
        Assert.DoesNotContain("FugaTES.Integracao.SAP", CarregarGrafoProjetos()["FugaTES.WinForms"]);
    }

    [Fact]
    public void TA04_WinForms_NaoReferencia_IntegracaoToledo()
    {
        Assert.DoesNotContain("FugaTES.Integracao.Toledo", CarregarGrafoProjetos()["FugaTES.WinForms"]);
    }

    [Fact]
    public void TA05_HostIntegracao_Referencia_Aplicacao()
    {
        Assert.Contains("FugaTES.Aplicacao", CarregarGrafoProjetos()["FugaTES.HostIntegracao"]);
    }

    [Fact]
    public void TA06_HostIntegracao_NaoReferencia_WinForms()
    {
        Assert.DoesNotContain("FugaTES.WinForms", CarregarGrafoProjetos()["FugaTES.HostIntegracao"]);
    }

    [Fact]
    public void TA07_IntegracaoSap_NaoReferencia_IntegracaoToledo()
    {
        Assert.DoesNotContain("FugaTES.Integracao.Toledo", CarregarGrafoProjetos()["FugaTES.Integracao.SAP"]);
    }

    [Fact]
    public void TA08_IntegracaoToledo_NaoReferencia_IntegracaoSap()
    {
        Assert.DoesNotContain("FugaTES.Integracao.SAP", CarregarGrafoProjetos()["FugaTES.Integracao.Toledo"]);
    }

    [Fact]
    public void TA09_Persistencia_NaoReferencia_Integracoes()
    {
        var referencias = CarregarGrafoProjetos()["FugaTES.Persistencia"];

        Assert.DoesNotContain("FugaTES.Integracao.SAP", referencias);
        Assert.DoesNotContain("FugaTES.Integracao.Toledo", referencias);
    }

    [Fact]
    public void TA10_GrafoInterno_NaoContem_ReferenciaCircular()
    {
        var grafo = CarregarGrafoProjetos();

        foreach (var projeto in grafo.Keys)
        {
            Assert.False(PossuiCiclo(projeto, projeto, grafo, []), $"Ciclo detectado a partir de {projeto}.");
        }
    }

    [Fact]
    public void TA11_PainelInicialForm_PodeSerConstruido_SemAcessoExterno()
    {
        using var form = new PainelInicialForm();

        Assert.Equal("FugaTES", form.Text);
    }

    [Fact]
    public void TA12_HostIntegracao_PodeSerConstruido_Estruturalmente_SemAcessoExterno()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FUGATES_INTEGRATION_WORKER_ENABLED"] = null,
            ["HostIntegracao:PollInterval"] = "00:00:01",
            ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
            ["HostIntegracao:ExternalMode"] = "Fake",
            ["HostIntegracao:PersistenceMode"] = "Fake",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "00:05:00",
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:45"
        });
        HostIntegracaoComposition.Configurar(builder);

        using var host = builder.Build();

        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), servico => servico is HospedagemIntegracaoWorker);
    }
    private static Dictionary<string, string[]> CarregarGrafoProjetos()
    {
        var raiz = LocalizarRaizRepositorio();
        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                caminho => Path.GetFileNameWithoutExtension(caminho),
                caminho => ObterReferenciasProjeto(caminho).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ObterReferenciasProjeto(string caminhoProjeto)
    {
        var diretorio = Path.GetDirectoryName(caminhoProjeto)!;
        var documento = XDocument.Load(caminhoProjeto);

        return documento.Descendants("ProjectReference")
            .Select(elemento => elemento.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(diretorio, include!)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(nomeProjeto => !string.IsNullOrWhiteSpace(nomeProjeto))
            .Select(nomeProjeto => nomeProjeto!);
    }

    private static string[] ObterPacotes(string nomeProjeto)
    {
        var raiz = LocalizarRaizRepositorio();
        var caminhoProjeto = Directory.EnumerateFiles(Path.Combine(raiz, "src"), $"{nomeProjeto}.csproj", SearchOption.AllDirectories).Single();
        var documento = XDocument.Load(caminhoProjeto);

        return documento.Descendants("PackageReference")
            .Select(elemento => elemento.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
    }

    private static bool PossuiCiclo(string origem, string atual, IReadOnlyDictionary<string, string[]> grafo, HashSet<string> visitados)
    {
        if (!grafo.TryGetValue(atual, out var referencias))
        {
            return false;
        }

        foreach (var referencia in referencias.Where(grafo.ContainsKey))
        {
            if (referencia.Equals(origem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (visitados.Add(referencia) && PossuiCiclo(origem, referencia, grafo, visitados))
            {
                return true;
            }
        }

        return false;
    }

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);

        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }
}
