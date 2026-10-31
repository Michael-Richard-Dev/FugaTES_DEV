using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Runtime.Loader;
using FugaTES.Integracao.Guardian;
using Xunit;

namespace R165U.Automatic.Tests;

public sealed class R165ACValidationDiagnosticsTests
{
    private const string Token = "SYNTHETIC_R165AC_SECRET_NOT_A_REAL_CREDENTIAL";
    private const string Field = "dadosProtocoloPreCadastro.codigoContrato";
    private const string Message = "The codigoContrato field is required.";

    [Theory]
    [InlineData("codigoContrato")]
    [InlineData("dadosProtocoloPreCadastro.codigoContrato")]
    [InlineData("codigoItem")]
    [InlineData("codigoEmissor")]
    [InlineData("idAgenda")]
    [InlineData("numeroReferencia")]
    [InlineData("configuracao.urlParaRetorno")]
    [InlineData("dadosProtocoloPreCadastro.camposAdicionais[0].valor")]
    public void Root_validation_dictionary_preserves_only_known_field_and_safe_template(string field)
    {
        // Synthetic conventional validation shape, NOT a reconstruction of the lost Guardian body.
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new[] { Message } });
        Assert.Equal(Message, Read("ReadSafeBusinessMessage", body));
        using var safe = JsonDocument.Parse(Read("ReadSafeResponseBody", body)!);
        Assert.Equal(Message, safe.RootElement.GetProperty(field)[0].GetString());
        Assert.Null(Read("ReadSafeErrorCode", body));
    }

    [Theory]
    [InlineData("ModelState")]
    [InlineData("validationErrors")]
    [InlineData("errors")]
    public void Conventional_validation_container_keeps_technical_field_name(string container)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [container] = new Dictionary<string, object> { [Field] = new[] { Message } }
        });
        Assert.Equal(Message, Read("ReadSafeBusinessMessage", body));
        using var safe = JsonDocument.Parse(Read("ReadSafeResponseBody", body)!);
        Assert.Equal(Message, safe.RootElement.GetProperty(container).GetProperty(Field)[0].GetString());
    }

    [Fact]
    public void Empty_model_key_accepts_only_a_safe_model_validation_message()
    {
        const string body = "{\"\":[\"The request is invalid.\"]}";
        Assert.Equal("The request is invalid.", Read("ReadSafeBusinessMessage", body));
        using var safe = JsonDocument.Parse(Read("ReadSafeResponseBody", body)!);
        Assert.Equal("The request is invalid.", safe.RootElement.GetProperty("")[0].GetString());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Authorization")]
    [InlineData("signature")]
    [InlineData("email")]
    [InlineData("documentoEmissor")]
    [InlineData("opaque-key-secret")]
    [InlineData("person@example.invalid")]
    public void Unknown_or_sensitive_field_names_do_not_classify_arbitrary_arrays_as_messages(string field)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new[] { "opaque-secret" } });
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.DoesNotContain(field, safe);
        Assert.DoesNotContain("opaque-secret", safe);
    }

    [Theory]
    [InlineData("opaque-secret")]
    [InlineData("person@example.invalid")]
    [InlineData("123.456.789-00")]
    [InlineData("12.345.678/0001-90")]
    [InlineData("Bearer short-secret")]
    [InlineData("password=short-secret")]
    [InlineData("The codigoContrato field is required. short-secret")]
    [InlineData("The token field is required.")]
    [InlineData("The 12345678901 field is required.")]
    public void Known_technical_field_does_not_make_an_unclassified_value_safe(string value)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { [Field] = new[] { value } });
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.DoesNotContain(value, safe);
        Assert.Contains("[REDACTED]", safe);
    }

    [Fact]
    public void Mixed_validation_array_keeps_useful_message_but_not_secrets_or_identifiers()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [Field] = new[] { Token, Message, "person@example.invalid", "12345678901" }
        });
        Assert.Equal(Message, Read("ReadSafeBusinessMessage", body));
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.Contains(Message, safe);
        foreach (var secret in new[] { Token, "person@example.invalid", "12345678901" }) Assert.DoesNotContain(secret, safe);
    }

    [Fact]
    public void Unknown_container_scalar_and_nested_credential_stay_redacted()
    {
        const string body = "{\"ModelState\":{\"password\":{\"message\":\"opaque-secret\"},\"other\":[\"opaque-secret\"]},\"opaque\":\"other-secret\"}";
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.DoesNotContain("opaque-secret", safe);
        Assert.DoesNotContain("other-secret", safe);
    }

    [Theory]
    [InlineData("<html>unclassified secret</html>")]
    [InlineData("not-json")]
    [InlineData("{\"codigoContrato\":[\"The codigoContrato field is required.\"]")]
    [InlineData("{\"codigoContrato\":[\"The codigoContrato field is required.\"],\"CodigoContrato\":[]}")]
    public void Non_json_malformed_or_ambiguous_responses_fail_closed(string body)
    {
        foreach (var method in new[] { "ReadSafeBusinessMessage", "ReadSafeResponseBody", "ReadSafeErrorCode" }) Assert.Null(Read(method, body));
    }

    [Fact]
    public void Large_validation_response_is_not_partially_returned()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { [Field] = new[] { Message, new string('x', 8200) } });
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        Assert.Null(Read("ReadSafeResponseBody", body));
    }

    [Theory]
    [InlineData("opaque-secret", false)]
    [InlineData("opaque-secret", true)]
    [InlineData("123.456.789-00", false)]
    [InlineData("123.456.789-00", true)]
    [InlineData("person@example.invalid", false)]
    [InlineData("person@example.invalid", true)]
    public void Error_dictionary_does_not_bypass_value_classification(string sensitive, bool unknownField)
    {
        var body = JsonSerializer.Serialize(new { errors = new Dictionary<string, object>
        {
            [unknownField ? "unclassified" : Field] = new[] { sensitive }
        } });
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        Assert.DoesNotContain(sensitive, Read("ReadSafeResponseBody", body)!);
    }

    [Fact]
    public void Recognized_business_message_redacts_cpf_without_discarding_safe_prefix()
    {
        const string body = "{\"message\":\"Documento rejeitado: 123.456.789-00\"}";
        Assert.DoesNotContain("123.456.789-00", Read("ReadSafeResponseBody", body)!);
        Assert.StartsWith("Documento rejeitado:", Read("ReadSafeBusinessMessage", body));
    }

    [Fact]
    public void Diagnostic_delta_preserves_existing_public_Guardian_binary_contract()
    {
        var root = new DirectoryInfo(Environment.GetEnvironmentVariable("FUGATES_TEST_EVIDENCE_ROOT") ?? AppContext.BaseDirectory);
        const string relative = "evidence/R165Y_GUARDIAN_PROTOCOL_TAB/delivery_R1/backend/FugaTES.Integracao.Guardian.dll";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative))) root = root.Parent;
        Assert.NotNull(root);
        var baselinePath = Path.Combine(root.FullName, relative);
        var context = new AssemblyLoadContext("R165AC_BASELINE_API_ONLY", isCollectible: true);
        context.Resolving += (_, name) =>
        {
            var path = Path.Combine(Path.GetDirectoryName(baselinePath)!, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            static string[] Surface(Assembly assembly) => assembly.GetExportedTypes()
                .Where(type => type.FullName is not
                    ("FugaTES.Integracao.Guardian.Transportadoras.MasterdataPrivateHttpBindingR159B" or
                     "FugaTES.Integracao.Guardian.Transportadoras.MasterdataCallbackUrlPolicyR159B"))
                .SelectMany(type =>
                new[] { type.FullName! }.Concat(type.GetMembers(BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static)
                    .Where(member => !(type == assembly.GetType("FugaTES.Integracao.Guardian.Transportadoras.GuardianCarrierCreateFieldsR37") &&
                        member.Name is "PrivateHttpCallback" or "get_PrivateHttpCallback" or "set_PrivateHttpCallback"))
                    .Select(member => type.FullName + ":" + member.MemberType + ":" + member)))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Assert.Equal(Surface(context.LoadFromAssemblyPath(baselinePath)), Surface(typeof(GuardianRestR57HttpException).Assembly));
        }
        finally { context.Unload(); }
    }

    private static string? Read(string method, string body)
        => (string?)typeof(GuardianRestR57HttpException).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Encoding.UTF8.GetBytes(body), Token]);
}
