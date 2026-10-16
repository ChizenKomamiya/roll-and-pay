using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Payments;

// Shared by preview and payment registration. Inspect the JSON token before converting it,
// so decimal/exponent notation cannot silently become an integer.
public static class PaymentRequest
{
    public const int MaxBodyBytes = 16 * 1024;

    public static async Task<long> ReadAsync(HttpRequest request)
    {
        if (request.QueryString.HasValue)
            throw new ApiException(400, "INVALID_REQUEST", "クエリは指定できません。");
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (contentType.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(415, "UNSUPPORTED_MEDIA_TYPE", "UTF-8のapplication/jsonを指定してください。");
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();

        // Read at most one byte beyond the limit, including when Content-Length is absent.
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), request.HttpContext.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxBodyBytes) throw TooLarge();
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            throw new ApiException(400, "INVALID_REQUEST", "JSONの形式を確認してください。");
        }
    }

    public static long Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw Invalid();
        var fields = body.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "totalYen") throw Invalid();
        var value = fields[0].Value;
        if (value.ValueKind != JsonValueKind.Number ||
            value.GetRawText().Any(c => c is < '0' or > '9') || !value.TryGetInt64(out var totalYen)) throw Invalid();
        return PaymentCalculator.ValidateTotal(totalYen);
    }

    private static ApiException Invalid() => new(422, "VALIDATION_ERROR", "totalYenに1〜9,999,999円のJSON整数だけを指定してください。");
    private static ApiException TooLarge() => new(413, "PAYLOAD_TOO_LARGE", "本文は16 KiB以下にしてください。");
}
