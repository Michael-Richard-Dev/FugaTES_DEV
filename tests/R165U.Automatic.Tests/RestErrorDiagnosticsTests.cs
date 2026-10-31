using System.Reflection;
using System.Text;
using System.Text.Json;
using FugaTES.Integracao.Guardian;
using Xunit;

namespace R165U.Automatic.Tests;

public sealed class RestErrorDiagnosticsTests
{
    private const string Token = "SYNTHETIC_TOKEN_abcdefghijklmnopqrstuvwxyz_123456789";

    [Fact]
    public void Existing_readers_find_nested_diagnostic_hints()
    {
        const string body = "{\"errors\":[{\"message\":\"Item inexistente\",\"code\":309851}]}";
        Assert.Equal("Item inexistente", Read("ReadSafeBusinessMessage", body));
        Assert.Equal("309851", Read("ReadSafeErrorCode", body));
    }

    [Fact]
    public void Exception_exposes_only_an_optional_safe_body_with_internal_init()
    {
        var property = typeof(GuardianRestR57HttpException).GetProperty("SafeResponseBody");
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.True(property.SetMethod?.IsAssembly);
        Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        var error = (Exception)Activator.CreateInstance(typeof(GuardianRestR57HttpException),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [400, "GUARDIAN_R57_HTTP_400", false, null], null)!;
        Assert.Null(property.GetValue(error));
        Assert.Null(error.InnerException);
        Assert.Equal("GUARDIAN_R57_HTTP_400", error.Message);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("mensagem")]
    [InlineData("detail")]
    [InlineData("title")]
    [InlineData("error")]
    [InlineData("errors")]
    [InlineData("error_description")]
    [InlineData("ExceptionMessage")]
    [InlineData("erro")]
    [InlineData("erros")]
    [InlineData("mensagemErro")]
    [InlineData("mensagens")]
    [InlineData("errorMessage")]
    public void Nested_recognized_messages_and_codes_are_preserved(string key)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["envelope"] = new[] { new Dictionary<string, object> { [key] = "Item inexistente", ["errorCode"] = 309851 } }
        });
        using var safe = JsonDocument.Parse(Read("ReadSafeResponseBody", body)!);
        var nested = safe.RootElement.EnumerateObject().Single().Value[0];
        Assert.Equal("Item inexistente", nested.GetProperty(key).GetString());
        Assert.Equal(309851, nested.GetProperty("errorCode").GetInt32());
        Assert.Equal("Item inexistente", Read("ReadSafeBusinessMessage", body));
        Assert.Equal("309851", Read("ReadSafeErrorCode", body));
    }

    [Theory]
    [InlineData("erro")]
    [InlineData("erros")]
    [InlineData("mensagemErro")]
    [InlineData("mensagens")]
    [InlineData("errorMessage")]
    public void Common_diagnostic_alias_envelopes_preserve_sanitized_field_messages(string key)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [key] = new Dictionary<string, object>
            {
                ["password"] = new[] { "opaque-secret" },
                ["field"] = new[] { "Campo obrigatorio; " + Token }
            }
        });
        var message = Read("ReadSafeBusinessMessage", body);
        Assert.NotNull(message);
        Assert.StartsWith("Campo obrigatorio;", message);
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.Contains("Campo obrigatorio;", safe);
        foreach (var value in new[] { safe, message })
        {
            Assert.True(!value.Contains(Token, StringComparison.Ordinal));
            Assert.True(!value.Contains("opaque-secret", StringComparison.Ordinal));
        }
        using var json = JsonDocument.Parse(safe);
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty(key).ValueKind);
    }

    [Fact]
    public void Root_precedence_and_existing_code_formats_are_preserved()
    {
        const string body = "{\"nested\":{\"message\":\"Nested\",\"code\":12},\"title\":\"Title\",\"message\":\"Root\",\"code\":\"E_INVALID\",\"errorCode\":\"GUARDIAN_REJECTED\"}";
        Assert.Equal("Root", Read("ReadSafeBusinessMessage", body));
        Assert.Equal("GUARDIAN_REJECTED", Read("ReadSafeErrorCode", body));
        Assert.Equal("Item inexistente", Read("ReadSafeBusinessMessage", "\"Item inexistente\""));
    }

    [Fact]
    public void Recognized_root_scalars_take_precedence_over_nested_hints()
    {
        const string body = "{\"message\":{\"message\":\"Nested\"},\"title\":\"Root\",\"errorCode\":{\"code\":42},\"code\":7}";
        Assert.Equal("Root", Read("ReadSafeBusinessMessage", body));
        Assert.Equal("7", Read("ReadSafeErrorCode", body));
    }

    [Fact]
    public void Arrays_keep_known_messages_but_not_unknown_scalar_values()
    {
        const string body = "[{\"errors\":[\"Item inexistente\",{\"codigoErro\":42,\"detail\":\"Dado rejeitado\"}]},\"opaque-secret\",123,true]";
        using var safe = JsonDocument.Parse(Read("ReadSafeResponseBody", body)!);
        Assert.Equal("Item inexistente", safe.RootElement[0].GetProperty("errors")[0].GetString());
        Assert.Equal(42, safe.RootElement[0].GetProperty("errors")[1].GetProperty("codigoErro").GetInt32());
        Assert.All(safe.RootElement.EnumerateArray().Skip(1), value => Assert.Equal("[REDACTED]", value.GetString()));
        Assert.Equal("Item inexistente", Read("ReadSafeBusinessMessage", body));
        Assert.Equal("42", Read("ReadSafeErrorCode", body));
    }

    [Theory]
    [InlineData("ReadSafeBusinessMessage", false)]
    [InlineData("ReadSafeResponseBody", false)]
    [InlineData("ReadSafeBusinessMessage", true)]
    [InlineData("ReadSafeResponseBody", true)]
    public void Model_state_shaped_errors_inherit_message_context(string reader, bool nested)
    {
        // Synthetic envelope coverage, not evidence of the original 1140 response or a Guardian contract.
        const string field = "dadosProtocoloPreCadastro.codigoItem";
        var value = nested ? "[{\"validation\":[{\"field\":[\"Campo obrigatorio\"]}]}]" : "[\"Campo obrigatorio\"]";
        var body = "{\"errors\":{\"" + field + "\":" + value + "}}";
        var safe = Read(reader, body);
        Assert.NotNull(safe);
        Assert.Contains("Campo obrigatorio", safe);
        Assert.Equal(reader == "ReadSafeResponseBody", safe.Contains(field, StringComparison.Ordinal));
        Assert.Null(Read("ReadSafeErrorCode", body));
        if (reader == "ReadSafeResponseBody")
        {
            using var json = JsonDocument.Parse(safe);
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("errors").ValueKind);
        }
    }

    [Fact]
    public void Inherited_message_context_excludes_nested_credential_subtrees()
    {
        const string body = "{\"errors\":{\"validation\":[{\"password\":[\"opaque-secret\"],\"credentialValue\":{\"field\":[\"opaque-secret\"]}}]}}";
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        Assert.Null(Read("ReadSafeErrorCode", body));
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.True(!safe.Contains("opaque-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Inherited_messages_are_sanitized_without_promoting_code_values()
    {
        var body = JsonSerializer.Serialize(new { errors = new Dictionary<string, object>
        {
            ["code"] = "E_INVALID", ["field"] = new[] { "Campo obrigatorio; " + Token + " password=opaque-secret" }
        } });
        var message = Read("ReadSafeBusinessMessage", body);
        Assert.NotNull(message);
        Assert.StartsWith("Campo obrigatorio;", message);
        foreach (var reader in new[] { "ReadSafeBusinessMessage", "ReadSafeResponseBody" })
        {
            var safe = Read(reader, body)!;
            Assert.True(!safe.Contains(Token, StringComparison.Ordinal));
            Assert.True(!safe.Contains("opaque-secret", StringComparison.Ordinal));
        }
        Assert.Equal("E_INVALID", Read("ReadSafeErrorCode", body));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("senha")]
    [InlineData("signature")]
    [InlineData("assinatura")]
    [InlineData("email")]
    [InlineData("Authorization")]
    [InlineData("bearer")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("api-key")]
    [InlineData("clientSecret")]
    [InlineData("credentialValue")]
    [InlineData("credentials")]
    [InlineData("username")]
    public void Credential_subtrees_are_redacted_and_never_searched(string key)
    {
        var body = JsonSerializer.Serialize(new { errors = new[] { new Dictionary<string, object>
        {
            [key] = new { message = "opaque-secret", code = 123 }, ["detail"] = "Item inexistente"
        } } });
        var safe = Read("ReadSafeResponseBody", body);
        Assert.NotNull(safe);
        Assert.True(!safe.Contains("opaque-secret", StringComparison.Ordinal));
        Assert.Equal("Item inexistente", Read("ReadSafeBusinessMessage", body));
        Assert.Null(Read("ReadSafeErrorCode", body));
    }

    [Fact]
    public void Unrecognized_property_names_and_scalar_secrets_are_not_copied()
    {
        const string body = "{\"opaque-key-secret\":{\"opaque-field\":\"short-secret\",\"id\":12345,\"enabled\":true,\"code\":\"short-secret\"},\"detail\":\"Item inexistente\"}";
        var safe = Read("ReadSafeResponseBody", body)!;
        foreach (var secret in new[] { "opaque-key-secret", "opaque-field", "short-secret", "12345" })
            Assert.True(!safe.Contains(secret, StringComparison.Ordinal));
        Assert.Contains("Item inexistente", safe);
    }

    [Fact]
    public void Token_only_body_has_no_business_message_or_error_code()
    {
        var body = JsonSerializer.Serialize(new { token = Token });
        Assert.Null(Read("ReadSafeBusinessMessage", body));
        Assert.Null(Read("ReadSafeErrorCode", body));
        Assert.True(!Read("ReadSafeResponseBody", body)!.Contains(Token, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Bearer short-secret")]
    [InlineData("password=short-secret")]
    [InlineData("signature: short-secret")]
    [InlineData("person@example.invalid")]
    [InlineData("email short-secret")]
    public void Message_sanitization_applies_at_every_level(string sensitive)
    {
        var body = JsonSerializer.Serialize(new { errors = new[] { new { detail = "Item inexistente; " + sensitive } } });
        var safe = Read("ReadSafeResponseBody", body)!;
        Assert.True(!safe.Contains("short-secret", StringComparison.Ordinal));
        Assert.True(!safe.Contains("person@example.invalid", StringComparison.Ordinal));
        Assert.Contains("Item inexistente", safe);
        Assert.Contains("[REDACTED]", safe);
    }

    [Fact]
    public void Exact_token_is_removed_before_message_truncation_and_from_keys()
    {
        var prefix = new string('x', 245) + " ";
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [Token] = "opaque-secret", ["errors"] = new[] { prefix + Token + " suffix" }
        });
        var safe = Read("ReadSafeResponseBody", body)!;
        var message = Read("ReadSafeBusinessMessage", body)!;
        Assert.True(!safe.Contains(Token, StringComparison.Ordinal));
        Assert.True(!safe.Contains(Token[..10], StringComparison.Ordinal));
        Assert.True(!message.Contains(Token[..10], StringComparison.Ordinal));
        Assert.InRange(message.Length, 1, 256);
    }

    [Theory]
    [InlineData("{\"message\":\"Good\",\"MESSAGE\":\"Other\"}")]
    [InlineData("{\"message\":\"Good\",\"errors\":[{\"code\":1,\"Code\":2}]}")]
    [InlineData("{\"message\":\"Good\",\"password\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"message\":\"Good\",\"errors\":{\"code\":1,\"co\\u0064e\":2}}")]
    [InlineData("{\"message\":\"Good\"")]
    [InlineData("{\"message\":\"Good\",\"code\":1e9999}")]
    [InlineData("{\"message\":\"\\uD800\"}")]
    [InlineData("{\"unknown\":\"\\uD800\"}")]
    [InlineData("{\"\\uD800\":\"unknown\"}")]
    [InlineData("not-json")]
    [InlineData("")]
    public void Invalid_or_ambiguous_json_fails_closed_for_all_readers(string body) => AssertRejected(Encoding.UTF8.GetBytes(body));

    [Fact]
    public void Invalid_utf8_fails_closed() => AssertRejected([123, 34, 109, 101, 115, 115, 97, 103, 101, 34, 58, 34, 255, 34, 125]);

    [Fact]
    public void Input_byte_limit_is_inclusive_and_oversize_is_rejected()
    {
        var body = "{\"message\":\"Item inexistente\"}";
        Assert.NotNull(Read("ReadSafeResponseBody", body.PadRight(8192)));
        AssertRejected(Encoding.UTF8.GetBytes(body.PadRight(8193)));
        AssertRejected(null);
    }

    [Fact]
    public void Depth_limit_is_inclusive_and_checked_even_under_credentials()
    {
        var allowed = new string('[', 8) + "0" + new string(']', 8);
        Assert.NotNull(Read("ReadSafeResponseBody", allowed));
        AssertRejected(Encoding.UTF8.GetBytes("[" + allowed + "]"));
        AssertRejected(Encoding.UTF8.GetBytes("{\"password\":" + allowed + "}"));
    }

    [Fact]
    public void Node_limit_is_inclusive_and_overflow_is_rejected()
    {
        var allowed = "[" + string.Join(",", Enumerable.Repeat("0", 127)) + "]";
        Assert.NotNull(Read("ReadSafeResponseBody", allowed));
        AssertRejected(Encoding.UTF8.GetBytes(allowed[..^1] + ",0]"));
    }

    [Fact]
    public void Sanitized_output_is_bounded_even_when_redaction_expands_input()
    {
        var body = "[" + string.Join(",", Enumerable.Repeat("{\"message\":\"" + new string('\u00e9', 35) + "\"}", 60)) + "]";
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 8192);
        Assert.Null(Read("ReadSafeResponseBody", body));
    }

    private static void AssertRejected(byte[]? body)
    {
        foreach (var method in new[] { "ReadSafeResponseBody", "ReadSafeBusinessMessage", "ReadSafeErrorCode" })
            Assert.Null(Invoke(method, body));
    }

    private static string? Read(string method, string body) => Invoke(method, Encoding.UTF8.GetBytes(body));

    private static string? Invoke(string method, byte[]? body)
    {
        // Reflection exercises the internal boundary without changing assembly visibility or adapter ownership.
        var reader = typeof(GuardianRestR57HttpException).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(reader);
        return (string?)reader.Invoke(null, [body, Token]);
    }
}
