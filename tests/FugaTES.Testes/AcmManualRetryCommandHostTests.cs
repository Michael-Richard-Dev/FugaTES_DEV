using System.Reflection;
using FugaTES.Callback.Http;

public sealed class AcmManualRetryCommandHostTests
{
    [Theory]
    [InlineData(new string[] { }, false)]
    [InlineData(new[] { "--acm-manual-retry-command" }, true)]
    public void Dedicated_startup_is_selected_only_by_its_explicit_flag(string[] args, bool expected)
    {
        var type = typeof(AcmManualRetryEndpoint).Assembly.GetType("FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandHost");
        Assert.NotNull(type);
        var selected = type.GetMethod("Selected", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(expected, selected.Invoke(null, new object[] { args }));
    }

    [Theory]
    [InlineData("--acm-manual-retry-command", "--dev-unified-read-only")]
    [InlineData("--dev-unified-read-only", "--acm-manual-retry-command")]
    [InlineData("--acm-manual-retry-command", "--urls=http://0.0.0.0:18763")]
    [InlineData("--acm-manual-retry-command=1")]
    public void Dedicated_startup_rejects_mixed_or_extended_arguments(params string[] args)
    {
        var type = typeof(AcmManualRetryEndpoint).Assembly.GetType("FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandHost");
        Assert.NotNull(type);
        var exception = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Selected")!.Invoke(null, new object[] { args }));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }
}

