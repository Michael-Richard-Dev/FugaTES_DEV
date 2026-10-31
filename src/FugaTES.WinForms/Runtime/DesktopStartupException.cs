namespace FugaTES.WinForms.Runtime;

public sealed class DesktopStartupException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
