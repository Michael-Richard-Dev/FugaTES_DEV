using System.Text.Json;
using FugaTES.WinForms.Monitor;
using static FugaTES.Testes.GuardianProtocolClientTests;

namespace FugaTES.Testes;

public sealed class GuardianProtocolReadbackTests
{
    [Fact]
    public async Task Startup_profile_loads_the_explicit_readback_key_relative_to_profile_directory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "protocol-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "recorded.json"), JsonSerializer.Serialize(Snapshot()));
            await File.WriteAllTextAsync(Path.Combine(directory, "AcmMonitor.R165M.json"), JsonSerializer.Serialize(new AcmMonitorProfile
            { ProtocolPreviewReadbackFile = "recorded.json" }));
            using var session = GuardianProtocolSession.Load(directory);
            var result = await session.Client.PreviewAsync(Id, default);
            Assert.Equal(Payload, result.PayloadJson);
            Assert.False(result.CanSend);
            Assert.False(session.Client.Configured);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "recorded.json"));
            File.Delete(Path.Combine(directory, "AcmMonitor.R165M.json"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Local_export_uses_same_dto_and_exact_json_but_never_enables_send_even_if_tampered()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Snapshot() with
            {
                CanSend = true, State = "READY", HttpStatus = 400, BlockCode = null, PreviewId = null,
                SafeBusinessMessage = null, SafeResponseBody = null, ProtocolId = null
            }));
            using var session = GuardianProtocolSession.Create(new(), (_, _) => throw new Exception("NO_VAULT"),
                new Handler(_ => throw new Exception("NO_IO")), previewReadbackFile: path);
            var controller = new GuardianProtocolController(session.Client);
            controller.Select(Id, "SYNTHETIC-ONLY");
            Assert.True(controller.CanGenerate);
            await controller.GeneratePreviewAsync(default);
            Assert.Equal(Payload, controller.Snapshot!.PayloadJson);
            Assert.Equal(Hash, controller.Snapshot.PayloadSha256);
            Assert.Null(controller.Snapshot.PreviewId);
            Assert.Equal(400, controller.Snapshot.HttpStatus);
            Assert.False(controller.Snapshot.CanSend);
            Assert.Equal("HISTORICAL_REJECTION_NO_REPLAY", controller.Snapshot.BlockCode);
            Assert.Equal("HISTORICAL_BACKEND_EXPORT", controller.Snapshot.State);
            Assert.Contains("HISTORICAL", controller.PreviewSource);
            Assert.Contains("BACKEND_COMMAND_DISABLED", controller.LockReason);
            Assert.False(controller.CanSend);
            await controller.SendAsync(true, default);
            Assert.False(controller.CanSend);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("id")]
    [InlineData("contract")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task Wrong_hash_or_scope_and_unavailable_artifact_never_show_payload(string defect)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var snapshot = Snapshot();
            if (defect == "hash") snapshot = snapshot with { PayloadSha256 = new string('0', 64) };
            if (defect == "id") snapshot = snapshot with { IntegrationId = Guid.NewGuid() };
            if (defect == "contract") snapshot = snapshot with { Contract = "OTHER" };
            if (defect != "missing") await File.WriteAllTextAsync(path, defect == "malformed" ? "{private-invalid" : JsonSerializer.Serialize(snapshot));
            using var session = GuardianProtocolSession.Create(new(), previewReadbackFile: path);
            var controller = new GuardianProtocolController(session.Client);
            controller.Select(Id, "SYNTHETIC-ONLY");
            await controller.GeneratePreviewAsync(default);
            Assert.Null(controller.Snapshot);
            Assert.False(controller.CanSend);
            Assert.DoesNotContain("private", controller.LockReason);
        }
        finally { File.Delete(path); }
    }
}
