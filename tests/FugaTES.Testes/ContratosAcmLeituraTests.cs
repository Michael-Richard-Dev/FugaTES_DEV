using FugaTES.Aplicacao.Consultas.ContratosAcm;
using Xunit;
using FugaTES.Persistencia.Consultas.ContratosAcm;
using FugaTES.WinForms;
using System.Reflection;
using System.Windows.Forms;

namespace FugaTES.Testes;

public sealed class ContratosAcmLeituraTests
{
    [Fact]
    public void SqlUsaCentroParametrizadoEIdentidadeGlobalDaUltimaVersao()
    {
        var sql = ContratosAcmPostgreSqlReadRepository.ConsultaSql;
        Assert.Contains("payload_contrato->>'Centro' = @centro", sql);
        Assert.Contains("v.versao = identidade.ultima_versao", sql);
        Assert.Contains("v.payload_contrato->>'StatusContrato'", sql);
        Assert.DoesNotContain("3011", sql);
        Assert.DoesNotContain("update", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TelaIndependenteSomenteLeituraComCentroEditavel()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new FrmContratosAcm(new ContratosAcmConsulta(new Memoria()));
                T Field<T>(string name) => (T)typeof(FrmContratosAcm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var filter = Field<TextBox>("centro");
                Assert.Equal("3011", filter.Text);
                Assert.False(filter.ReadOnly);
                filter.Text = "3008";
                Assert.Equal("3008", filter.Text);
                var grid = Field<DataGridView>("grid");
                Assert.True(grid.ReadOnly);
                Assert.False(grid.AllowUserToAddRows);
                Assert.False(grid.AllowUserToDeleteRows);
                Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(), c => c.DataPropertyName == "VersaoMaisRecente");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        Assert.Null(error);
    }

    [Fact]
    public async Task FiltroEditavelEIsoladoPreservaVersoesEValores()
    {
        var repo = new Memoria();
        var consulta = new ContratosAcmConsulta(repo);
        Assert.Equal("3011", ContratosAcmConsulta.CentroInicial);
        var rows = await consulta.ConsultarAsync(" 3011 ", default);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("3011", r.Centro));
        Assert.False(rows[0].VersaoMaisRecente);
        Assert.True(rows[1].VersaoMaisRecente);
        Assert.Null(rows[0].StatusContrato);
        Assert.Single(await consulta.ConsultarAsync("3008", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CentroAusenteNaoConsulta(string centro)
    {
        var repo = new Memoria();
        await Assert.ThrowsAsync<ArgumentException>(() => new ContratosAcmConsulta(repo).ConsultarAsync(centro, default));
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task AusenciaDeRegistrosDiferenteDeFalha()
    {
        var repo = new Memoria();
        var consulta = new ContratosAcmConsulta(repo);
        Assert.Empty(await consulta.ConsultarAsync("9999", default));
        repo.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => consulta.ConsultarAsync("3011", default));
    }

    [Fact]
    public async Task CancelamentoPropagado()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var repo = new Memoria();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ContratosAcmConsulta(repo).ConsultarAsync("3011", cts.Token));
    }

    private sealed class Memoria : IContratosAcmReadRepository
    {
        public int Calls;
        public bool Fail;
        public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException();
            ContratoAcmLeitura Row(string c, long v, bool latest) => new(c, "ACM", v,
                DateTime.UnixEpoch, null, "PENDENTE", Guid.Empty, "origem", latest);
            return Task.FromResult<IReadOnlyList<ContratoAcmLeitura>>(
                new[] { Row("3011", 1, false), Row("3011", 2, true), Row("3008", 1, true) }
                .Where(r => r.Centro == centro).ToArray());
        }
    }
}
