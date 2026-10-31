using System.Reflection;
using System.Reflection.Emit;
using FugaTES.WinForms;
using FugaTES.WinForms.Monitor;
using Npgsql;

namespace FugaTES.Testes;

public sealed class AcmMonitorStartupTests
{
    [Fact]
    public void Compiled_entrypoint_has_unified_safe_startup_and_explicit_read_monitor_mode()
    {
        var assembly = typeof(FrmMonitorMvp).Assembly;
        Assert.Equal("FugaTES", assembly.GetName().Name);
        // A finalidade continua sendo: o artefato compilado tem que corresponder exatamente a
        // release declarada pela propria aplicacao. Em vez de um literal que defasa em silencio
        // a cada promocao de versao (era 1.2.3 enquanto a baseline ja era 1.2.4), a expectativa
        // passa a ser ancorada em UnifiedDesktopSession.Version, que e o que o Desktop exibe e
        // o que a Hera homologa. Assim, divergencia entre binario e versao declarada continua
        // falhando, e promover a versao em um unico lugar mantem o teste coerente.
        var declarada = Version.Parse(UnifiedDesktopSession.Version);
        Assert.Equal(new Version(declarada.Major, declarada.Minor, declarada.Build, 0), assembly.GetName().Version);
        Assert.Equal($"{declarada.Major}.{declarada.Minor}.{declarada.Build}.0",
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        // Baseline comprovada neste gate, para que a ancoragem nao possa apontar para qualquer coisa.
        Assert.Equal("1.2.11", UnifiedDesktopSession.Version);
        var main = assembly.GetType("FugaTES.WinForms.Program")!.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
        var calls = Calls(main).ToArray();
        Assert.Contains(calls,x=>x.DeclaringType==typeof(UnifiedDesktopStartup)&&x.Name=="Run");
        Assert.Contains(calls, x => x.DeclaringType == typeof(AcmMonitorSession) && x.Name == "Carregar");
        Assert.Contains(calls, x => x.DeclaringType == typeof(FrmMonitorMvp) && x.IsConstructor);
        Assert.DoesNotContain(calls, x => x.DeclaringType?.Name is "DesktopRuntime" or "CarrierDesktopComposition" or "PainelInicialForm");
        Assert.DoesNotContain(calls, x => x.Name.Contains("HostIntegracao") || x.Name.Contains("Worker") || x.Name.Contains("CreateReadSource"));
        Assert.DoesNotContain(calls, x => x.DeclaringType?.Name is "WindowsCredentialProvider" or "NpgsqlConnection" or "NpgsqlDataSource");
    }

    [Fact]
    public void A_cleared_synthetic_profile_can_build_read_only_source_without_connecting()
    {
        var calls = 0;
        string? expectedUsername = null;
        using var session = AcmMonitorSession.Criar(new()
        { Enabled = true, LocalPort = 15433, CredentialTarget = "FugaTES/HML/ACM/PostgreSQL", ClearanceReference = "TEST-NOT-AUTHORIZATION", TransportSslMode = "Require", TransportPolicyReference = "SYNTHETIC-POLICY-NOT-AUTHORIZATION" },
            (_, username) => { calls++; expectedUsername = username; return "SYNTHETIC-ONLY"; });
        Assert.True(session.Configurado); Assert.Equal(1, calls);
        Assert.Equal("fugates_acm_hml_app", expectedUsername);
        var source = Assert.IsAssignableFrom<NpgsqlDataSource>(typeof(AcmMonitorSession)
            .GetField("source", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session));
        var connection = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        Assert.Equal("fugates_acm_hml_app", connection.Username);
        Assert.Equal("127.0.0.1", connection.Host); Assert.Equal(15433, connection.Port);
        Assert.Equal("fugates_dev", connection.Database); Assert.Equal("fugates", connection.SearchPath);
        Assert.Equal("-c default_transaction_read_only=on", connection.Options);
        Assert.False(connection.PersistSecurityInfo); Assert.False(connection.IncludeErrorDetail);
        Assert.Equal(SslMode.Require, connection.SslMode);
        Assert.Equal(GssEncryptionMode.Disable, connection.GssEncryptionMode);
        using var oldCredentialOnly = AcmMonitorSession.Criar(new()
        { Enabled = true, LocalPort = 15433, CredentialTarget = "FugaTES/HML/ACM/PostgreSQL", ClearanceReference = "TEST-NOT-AUTHORIZATION", TransportSslMode = "Require", TransportPolicyReference = "SYNTHETIC-POLICY-NOT-AUTHORIZATION" },
            (_, username) => username == "fugates_dev_app" ? "SYNTHETIC-OLD-ONLY" : throw new InvalidOperationException("SYNTHETIC_USERNAME_MISMATCH"));
        Assert.False(oldCredentialOnly.Configurado);
        // No repository method is called: this test never opens a socket.
    }

    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(c => unchecked((ushort)c.Value));
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort code = bytes[offset++];
            if (code == 0xfe) code = (ushort)(0xfe00 | bytes[offset++]);
            var op = codes[code];
            if (op.OperandType == OperandType.InlineMethod)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset))!;
            offset += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4
            };
        }
    }
}
