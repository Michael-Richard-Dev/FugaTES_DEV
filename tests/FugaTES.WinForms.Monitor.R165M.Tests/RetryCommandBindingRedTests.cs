using FugaTES.WinForms.Monitor;

public sealed class RetryCommandBindingRedTests
{
    [Fact] public void Desktop_has_an_optional_dedicated_command_binding()
        =>Assert.NotNull(typeof(UnifiedDesktopSession).Assembly.GetType("FugaTES.WinForms.ManualRetry.RetryCommandDesktopBinding"));
}
