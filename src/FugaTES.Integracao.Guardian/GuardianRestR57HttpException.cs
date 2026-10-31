using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Servicos.Mvp;

namespace FugaTES.Integracao.Guardian;

public sealed class GuardianRestR57HttpException : Exception, IAcmGuardianHttpFailure
{
    // Zero means no HTTP response was received. No untrusted body or inner exception is retained.
    public int HttpStatusCode { get; }
    public string SafeCode { get; }
    public string? SafeBusinessMessage { get; }
    public bool ResultadoIndeterminado { get; }
    public string? GuardianErrorCode { get; internal init; }
    public string? RequestId { get; internal init; }
    public string? CorrelationId { get; internal init; }
    public string? RequestPayloadFingerprint { get; internal init; }
    public string? ResponseRequestId { get; internal init; }
    public string? SafeResponseBody { get; internal init; }

    private const int MaximumBodyBytes = 8192;
    private const int MaximumNodes = 128;
    private const string Redacted = "[REDACTED]";
    // Diagnostic hints only; these names do not define a Guardian response contract.
    private static readonly string[] MessageKeys = ["message", "mensagem", "detail", "title", "error", "errors", "error_description", "exceptionmessage",
        "erro", "erros", "mensagemErro", "mensagens", "errorMessage"];
    private static readonly string[] CodeKeys = ["errorCode", "codigoErro", "code", "codigo"];
    private static readonly string[] ValidationContainers = ["ModelState", "validationErrors"];

    private static bool IsValidationField(string name)
        => name.Length <= 96 && !IsCredentialKey(name) && Regex.IsMatch(name,
            @"^(?:(?:dadosProtocoloPreCadastro|configuracao)\.)?(?:id|idAgenda|codigoContrato|codigoItem|codigoEmissor|codigoFluxo|codigoTransportadora|numeroReferencia|urlParaRetorno|camposAdicionais(?:\[[0-9]{1,2}\])?\.(?:numero|valor))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsValidationArray(JsonProperty property)
        => (property.Name.Length == 0 || IsValidationField(property.Name)) &&
            property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0 &&
            property.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String);

    private static string? ReadValidationMessage(JsonElement value, string token)
    {
        var raw = value.GetString()!;
        if (raw.Length > 256 || Sanitize(raw, token) is not { } safe) return null;
        var text = Regex.Replace(safe, @"(?:[.;\s]*\[REDACTED\])+$", "", RegexOptions.CultureInvariant).Trim();
        // A known field is not proof that an arbitrary value is a safe diagnostic.
        if (Regex.IsMatch(text, @"^(?:The request is invalid\.?|One or more validation errors occurred\.?|Campo obrigat\u00f3rio\.?|Campo obrigatorio\.?|Campo inv\u00e1lido\.?|Campo invalido\.?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return safe;
        var match = Regex.Match(text, @"^The (?<field>[A-Za-z0-9_.\[\]]{1,96}) field is (?:required|invalid)\.?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(text, @"^(?:O campo )?(?<field>[A-Za-z0-9_.\[\]]{1,96}) (?:e|\u00e9) (?:obrigat\u00f3rio|obrigatorio|inv\u00e1lido|invalido)\.?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && IsValidationField(match.Groups["field"].Value) ? safe : null;
    }

    internal GuardianRestR57HttpException(int status, string safeCode, bool unknown, string? safeBusinessMessage = null)
        : base(safeCode)
    {
        HttpStatusCode = status;
        SafeCode = safeCode;
        SafeBusinessMessage = safeBusinessMessage;
        ResultadoIndeterminado = unknown;
    }

    internal static string? ReadSafeBusinessMessage(byte[]? body, string token)
    {
        using var document = ReadValidatedDocument(body);
        return document is null ? null : FindDiagnostic(document.RootElement, token, codes: false,
            recognizedValue: document.RootElement.ValueKind == JsonValueKind.String);
    }

    internal static string BodyKind(byte[]? body)
    {
        if (body is null) return "NOT_RETAINED_OR_LIMIT_EXCEEDED";
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind switch
            {
                JsonValueKind.Object => "JSON_OBJECT", JsonValueKind.String => "JSON_STRING",
                JsonValueKind.Array => "JSON_ARRAY", _ => "JSON_OTHER"
            };
        }
        catch (JsonException) { return "NON_JSON"; }
    }

    internal static string? ReadSafeErrorCode(byte[]? body, string token)
    {
        using var document = ReadValidatedDocument(body);
        return document is null ? null : FindDiagnostic(document.RootElement, token, codes: true);
    }

    internal static string? ReadSafeResponseBody(byte[]? body, string token)
    {
        using var document = ReadValidatedDocument(body);
        if (document is null) return null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteSafeElement(writer, document.RootElement, token,
                document.RootElement.ValueKind == JsonValueKind.String, codes: false);
        if (buffer.Length > MaximumBodyBytes) return null;
        var result = Encoding.UTF8.GetString(buffer.ToArray());
        return !string.IsNullOrEmpty(token) && result.Contains(token, StringComparison.Ordinal) ? null : result;
    }

    private static JsonDocument? ReadValidatedDocument(byte[]? body)
    {
        if (body is null || body.Length > MaximumBodyBytes) return null;
        JsonDocument? document = null;
        try
        {
            // Validate even values that will be redacted, before returning any partial diagnostic.
            _ = new UTF8Encoding(false, true).GetCharCount(body);
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var nodes = 0;
            if (ValidateElement(document.RootElement, ref nodes)) return document;
            document.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            document?.Dispose();
            return null;
        }
    }

    private static bool ValidateElement(JsonElement element, ref int nodes)
    {
        if (++nodes > MaximumNodes) return false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !ValidateElement(property.Value, ref nodes)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (!ValidateElement(item, ref nodes)) return false;
        }
        else if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
        else if (element.ValueKind == JsonValueKind.Number && !element.TryGetDecimal(out _)) return false;
        return true;
    }

    private static string? FindDiagnostic(JsonElement element, string token, bool codes, bool recognizedValue = false, bool validationValues = false)
    {
        if (recognizedValue && element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return codes ? ReadCode(element, token) : element.ValueKind == JsonValueKind.String
                ? validationValues ? ReadValidationMessage(element, token) : Sanitize(element.GetString()!, token) : null;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindDiagnostic(item, token, codes, recognizedValue, validationValues);
                if (found is not null) return found;
            }
        }
        if (element.ValueKind != JsonValueKind.Object) return null;
        var keys = codes ? CodeKeys : MessageKeys;
        foreach (var name in keys)
        {
            var value = element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            // Preserve the existing root message-key priority, including an empty first message.
            if (!codes && value.ValueKind == JsonValueKind.String) return Sanitize(value.GetString()!, token);
            var found = codes ? ReadCode(value, token) : null;
            if (found is not null) return found;
        }
        foreach (var name in keys)
        {
            var value = element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
            var found = FindDiagnostic(value, token, codes, recognizedValue: true);
            if (found is not null) return found;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (IsCredentialKey(property.Name) || keys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!codes && !recognizedValue && IsValidationArray(property))
            {
                foreach (var item in property.Value.EnumerateArray())
                    if (ReadValidationMessage(item, token) is { } message) return message;
                continue;
            }
            var found = FindDiagnostic(property.Value, token, codes,
                recognizedValue: !codes && recognizedValue && !CodeKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
                validationValues: validationValues || (!codes && recognizedValue));
            if (found is not null) return found;
        }
        return null;
    }

    private static string? ReadCode(JsonElement value, string token)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() :
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return text is not null && (string.IsNullOrEmpty(token) || !text.Contains(token, StringComparison.Ordinal)) &&
            Regex.IsMatch(text, @"^(?:-?[0-9]{1,9}|(?:E_|GUARDIAN_)[A-Z0-9_]{1,40})$", RegexOptions.CultureInvariant)
            ? text : null;
    }

    private static bool IsCredentialKey(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return new[] { "authorization", "bearer", "token", "apikey", "secret", "password", "passwd", "pwd", "senha",
            "signature", "assinatura", "email", "credential", "username", "login" }.Any(normalized.Contains);
    }

    private static void WriteSafeElement(Utf8JsonWriter writer, JsonElement element, string token, bool message, bool codes, bool validationValues = false)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var index = 0;
            foreach (var property in element.EnumerateObject())
            {
                var isMessage = MessageKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase);
                var isCode = CodeKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase);
                var credential = IsCredentialKey(property.Name);
                var validation = IsValidationArray(property);
                var container = property.Value.ValueKind == JsonValueKind.Object &&
                    ValidationContainers.Contains(property.Name, StringComparer.OrdinalIgnoreCase);
                // Unknown property names can themselves contain credentials or personal data.
                writer.WritePropertyName(!credential && (isMessage || isCode || validation || container || (message && IsValidationField(property.Name)))
                    ? property.Name : $"[REDACTED_{++index}]");
                if (credential) writer.WriteStringValue(Redacted);
                else if (validation)
                {
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray()) writer.WriteStringValue(ReadValidationMessage(item, token) ?? Redacted);
                    writer.WriteEndArray();
                }
                else WriteSafeElement(writer, property.Value, token, isMessage || (message && !isCode), isCode,
                    validationValues: !isMessage && !isCode && (message || validationValues));
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteSafeElement(writer, item, token, message, codes, validationValues);
            writer.WriteEndArray();
        }
        else if (codes && ReadCode(element, token) is { } code)
        {
            if (element.ValueKind == JsonValueKind.Number) writer.WriteNumberValue(element.GetInt32());
            else writer.WriteStringValue(code);
        }
        else writer.WriteStringValue(message && element.ValueKind == JsonValueKind.String
            ? (validationValues ? ReadValidationMessage(element, token) : Sanitize(element.GetString()!, token)) ?? Redacted : Redacted);
    }

    internal static string? SafeResponseRequestId(string? value, string token)
    {
        if (value is null || value.Length > 80 || value.Contains(token, StringComparison.Ordinal)) return null;
        return Guid.TryParseExact(value, "D", out _) || Guid.TryParseExact(value, "N", out _) ||
            Regex.IsMatch(value, @"^(?:[0-9A-Z]{13,16}:[0-9A-F]{8}|00-[0-9a-f]{32}-[0-9a-f]{16}-0[01])$", RegexOptions.CultureInvariant)
            ? value : null;
    }

    private static string? Sanitize(string text, string token)
    {
        const string redacted = "[REDACTED]";
        var timeout = TimeSpan.FromMilliseconds(100);
        try
        {
            if (!string.IsNullOrEmpty(token)) text = text.Replace(token, redacted, StringComparison.Ordinal);
            // Redact before truncation; a cut credential must not survive as a prefix.
            text = Regex.Replace(text, @"\b(?:authorization|bearer|(?:access[_-]?|refresh[_-]?)?token|api[_-]?key|client[_-]?secret|password|senha|assinatura|signature|e-?mail)\b.*",
                redacted, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, timeout);
            text = Regex.Replace(text, @"(?<!\d)\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2}(?!\d)",
                redacted, RegexOptions.CultureInvariant, timeout);
            text = Regex.Replace(text, @"(?<!\d)\d{3}\.?\d{3}\.?\d{3}-?\d{2}(?!\d)",
                redacted, RegexOptions.CultureInvariant, timeout);
            text = Regex.Replace(text, @"[^\s<>""'@]+@[^\s<>""'@]+",
                redacted, RegexOptions.CultureInvariant, timeout);
            text = Regex.Replace(text, @"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\b|\b[A-Za-z0-9_+/=-]{32,}\b",
                redacted, RegexOptions.CultureInvariant, timeout);
            text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            text = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, timeout).Trim();
            return text.Length == 0 ? null : text[..Math.Min(text.Length, 256)];
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}
