using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace RollAndPay.Api.Http;

public static class UpdateRequest
{
    public const int MaxBodyBytes = 16 * 1024;
    public static string IfMatch(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("If-Match", out var values))
            throw new ApiException(428, "PRECONDITION_REQUIRED", "ゲームのETagを指定してください。");
        // A single strong opaque tag. Preserve its exact value in the request hash.
        var value = values.Count == 1 ? values[0] : null;
        if (value is null || value.Length < 2 || value[0] != '"' || value[^1] != '"' ||
            value.AsSpan(1, value.Length - 2).ContainsAnyExceptInRange((char)0x21, (char)0x7e) ||
            value.AsSpan(1, value.Length - 2).Contains('"'))
            throw new ApiException(400, "INVALID_REQUEST", "単一の強いETagを指定してください。");
        return value;
    }

    public static Guid Key(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values))
            throw new ApiException(428, "PRECONDITION_REQUIRED", "冪等性キーを指定してください。");
        if (values.Count != 1 || values[0]?.Length != 36 || !Guid.TryParseExact(values[0], "D", out var key) || key == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "冪等性キーの形式を確認してください。");
        return key;
    }

    public static async Task<JsonDocument> Body(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (type.CharSet is not null && !type.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(415, "UNSUPPORTED_MEDIA_TYPE", "UTF-8のJSONを指定してください。");
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted);
            if (count == 0) break;
            if (body.Length + count > MaxBodyBytes) throw TooLarge();
            body.Write(buffer, 0, count);
        }
        try { return JsonDocument.Parse(body.ToArray()); }
        catch (JsonException) { throw new ApiException(400, "INVALID_REQUEST", "JSONの形式を確認してください。"); }
    }

    // Call only after endpoint-specific validation. Object order/whitespace are irrelevant; array order is significant.
    public static string Hash(string method, string normalizedPath, JsonElement? body, string? originalIfMatch)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray(); writer.WriteStringValue(method.ToUpperInvariant()); writer.WriteStringValue(normalizedPath);
            writer.WriteBooleanValue(body.HasValue);
            if (body.HasValue) Canonical(writer, body.Value);
            writer.WriteStringValue(originalIfMatch); writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.Number) writer.WriteNumberValue(value.GetInt64());
        else value.WriteTo(writer);
    }

    private static ApiException TooLarge() => new(413, "PAYLOAD_TOO_LARGE", "本文は16 KiB以下にしてください。");
}
