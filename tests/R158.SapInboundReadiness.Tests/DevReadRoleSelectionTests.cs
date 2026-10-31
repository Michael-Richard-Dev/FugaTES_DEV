using FugaTES.Callback.Http;
using FugaTES.Persistencia.MotorIntegracao;

namespace R158.SapInboundReadiness.Tests;

// R164: a API privada 18762 passa a poder usar a identidade GET-only dedicada, por opt-in
// explicito. Offline: nenhuma conexao, nenhum processo iniciado.
public sealed class DevReadRoleSelectionTests
{
    private static void ComVariavel(string? valor, Action corpo)
    {
        var antigo = Environment.GetEnvironmentVariable(DevUnifiedReadApi.RoleEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DevUnifiedReadApi.RoleEnvironmentVariable, valor);
            corpo();
        }
        finally { Environment.SetEnvironmentVariable(DevUnifiedReadApi.RoleEnvironmentVariable, antigo); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_opt_in_a_identidade_permanece_a_operacional_compartilhada(string? valor)
        => ComVariavel(valor, () =>
            Assert.Equal(NpgsqlDataSourceFactory.AcmApplicationRole, DevUnifiedReadApi.RoleDeLeitura()));

    [Fact]
    public void Com_opt_in_a_identidade_passa_a_ser_a_get_only_dedicada()
        => ComVariavel(NpgsqlDataSourceFactory.DevReadOnlyApplicationRole, () =>
            Assert.Equal("fugates_dev_read_app", DevUnifiedReadApi.RoleDeLeitura()));

    [Theory]
    [InlineData("fugates_sap_inbound_app")]   // identidade de ESCRITA: jamais na API de leitura
    [InlineData("postgres")]
    [InlineData("fugates_dev_owner")]
    [InlineData("fugates_dev_app")]
    [InlineData("qualquer_outra")]
    public void Qualquer_outra_identidade_e_recusada(string role)
        => ComVariavel(role, () =>
        {
            var erro = Assert.Throws<InvalidOperationException>(() => DevUnifiedReadApi.RoleDeLeitura());
            Assert.Equal("UNIFIED_DEV_READ_ROLE_NOT_AUTHORIZED", erro.Message);
        });

    [Fact]
    public void A_identidade_escolhida_continua_passando_pelas_guardas_de_conexao()
    {
        // Ambas as identidades aceitas pela API sao guardadas; o destino continua imposto
        // pelo codigo, nao pela variavel.
        Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(NpgsqlDataSourceFactory.AcmApplicationRole));
        Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(NpgsqlDataSourceFactory.DevReadOnlyApplicationRole));
        var fonte = Fonte("DevUnifiedReadApi.cs");
        // A connection string segue fixando porta, base, schema e somente leitura no codigo.
        foreach (var fixo in new[] { "Port=5433", "Database=\"fugates_dev\"", "SearchPath=\"fugates\"",
                                     "default_transaction_read_only=on" })
            Assert.Contains(fixo.Replace("Port=5433", "Port=5433"), fonte.Replace("Port = 5433", "Port=5433"), StringComparison.Ordinal);
    }

    private static string Fonte(string nome)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Directory.EnumerateFiles(Path.Combine(dir!, "src"), nome, SearchOption.AllDirectories).Single());
    }
}
