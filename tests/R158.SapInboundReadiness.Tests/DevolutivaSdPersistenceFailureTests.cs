using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace R158.SapInboundReadiness.Tests;
public sealed class DevolutivaSdPersistenceFailureTests
{
    private const string Body = """
    {"SistemaBalanca":"Toledo","DataPesagem":"11/2/2026 2:17:37 PM","TipoPesagem":"S",
     "IdPesagem":"OFFLINE_SD_CORRELATION","Centro":"3011","PesoBruto":"1200,0","PesoTara":"500,0",
     "PesoAposDesconto":"700,0","camposAdicionais":[{"numero":1003,"valor":"4233"},{"numero":1009,"valor":""}]}
    """;
    private sealed class Repository(bool fail, bool afterCommit) : IDevolutivasSdRepositorio
    {
        public DevolutivaPesagemSd? Stored { get; private set; }
        public Task<IReadOnlyList<DevolutivaSdLinha>> ListarAsync(int limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RegistrarAsync(DevolutivaPesagemSd value, CancellationToken ct)
        {
            if (!fail || afterCommit) Stored=value;
            return fail ? Task.FromException(new IOException("SYNTHETIC_PRIVATE_DATABASE_DETAIL")) : Task.CompletedTask;
        }
    }
    private sealed class Logs : ILoggerProvider
    {
        public List<(string Message,Exception? Error)> Entries { get; }=[];
        public ILogger CreateLogger(string category)=>new Sink(Entries);
        public void Dispose(){}
        private sealed class Sink(List<(string Message,Exception? Error)> entries):ILogger
        {
            public bool IsEnabled(LogLevel level)=>true;
            public IDisposable? BeginScope<TState>(TState value) where TState:notnull=>null;
            public void Log<TState>(LogLevel level, EventId id,TState state,Exception? ex,Func<TState,Exception?,string> format)
                =>entries.Add((format(state,ex),ex));
        }
    }
    private static async Task<(int Status,JsonElement Body,Repository Repository,Logs Logs)> Call(bool fail=false,bool afterCommit=false)
    {
        var repo=new Repository(fail,afterCommit);var logs=new Logs();
        using var services=new ServiceCollection().AddSingleton<IDevolutivasSdRepositorio>(repo)
            .AddLogging(b=>b.ClearProviders().AddProvider(logs)).BuildServiceProvider();
        var c=new DefaultHttpContext{RequestServices=services};c.Request.Method="POST";c.Request.ContentType="application/json";
        var bytes=Encoding.UTF8.GetBytes(Body);c.Request.Body=new MemoryStream(bytes);c.Request.ContentLength=bytes.Length;c.Response.Body=new MemoryStream();
        await DevolutivaSdEndpoint.HandleAsync(c);c.Response.Body.Position=0;
        using var doc=await JsonDocument.ParseAsync(c.Response.Body);
        return(c.Response.StatusCode,doc.RootElement.Clone(),repo,logs);
    }
    [Fact]
    public async Task Partial_correlation_is_received_without_losing_original_document()
    {
        var r=await Call();Assert.Equal(200,r.Status);Assert.Equal("OK",r.Body.GetProperty("mensagem").GetString());
        Assert.Equal("SEM_CORRELACAO",r.Body.GetProperty("correlacao").GetString());Assert.Null(r.Repository.Stored!.OrdemVenda);
        Assert.Null(r.Repository.Stored.LinhaOrdem);Assert.Equal(Body,r.Repository.Stored.PayloadJson);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistence_error_never_claims_that_commit_did_not_happen(bool afterCommit)
    {
        var r=await Call(true,afterCommit);Assert.Equal(500,r.Status);Assert.NotEqual("OK",r.Body.GetProperty("mensagem").GetString());
        Assert.Equal("SD_DEVOLUTIVA_NAO_GRAVADA",r.Body.GetProperty("codigo").GetString());
        Assert.Equal("RESULTADO_INDETERMINADO",r.Body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null,r.Body.GetProperty("persisted").ValueKind);
        Assert.DoesNotContain("nada foi gravado",r.Body.GetRawText(),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pode reenviar",r.Body.GetRawText(),StringComparison.OrdinalIgnoreCase);
        Assert.Equal(afterCommit,r.Repository.Stored is not null);
    }
    [Fact]
    public async Task Persistence_error_does_not_log_exception_contents()
    {
        var r=await Call(true);var line=Assert.Single(r.Logs.Entries);Assert.Null(line.Error);
        Assert.Contains("SD_DEVOLUTIVA_NAO_GRAVADA",line.Message);Assert.Contains(nameof(IOException),line.Message);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_DATABASE_DETAIL",line.Message);Assert.DoesNotContain("SYNTHETIC_PRIVATE_DATABASE_DETAIL",r.Body.GetRawText());
    }
}
