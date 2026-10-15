using System.Text;
using Microsoft.AspNetCore.Http;

namespace RollAndPay.Api.Http;

public sealed class ReadSession
{
    public const string HeaderName = "X-Read-Session";
    public const int MaxEncodedLength = 8 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object ContextKey = new();
    public string? Token { get; private set; }
    private string? published;

    public static ReadSession Attach(HttpContext context)
    {
        if (context.Items.TryGetValue(ContextKey, out var existing)) return (ReadSession)existing!;
        var session = new ReadSession();
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            if (values.Count != 1) throw Invalid();
            session.Token = Decode(values[0]);
        }
        context.Items[ContextKey] = session;
        context.Response.OnStarting(() =>
        {
            if (session.published is not null) context.Response.Headers[HeaderName] = session.published;
            return Task.CompletedTask;
        });
        return session;
    }

    public void Accept(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Any(char.IsControl)) throw Data.CosmosErrors.Unavailable();
        string encoded;
        try { encoded = Convert.ToBase64String(Utf8.GetBytes(token)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        catch (EncoderFallbackException) { throw Data.CosmosErrors.Unavailable(); }
        if (encoded.Length > MaxEncodedLength) throw Data.CosmosErrors.Unavailable();
        Token = token;
        published = encoded;
    }

    public static string Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength || encoded.Length % 4 == 1 ||
            encoded.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw Invalid();
        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '='));
            var token = Utf8.GetString(bytes);
            if (token.Length == 0 || token.Any(char.IsControl) ||
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != encoded) throw Invalid();
            return token;
        }
        catch (FormatException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
    }

    public static ApiException Invalid() => new(400, "INVALID_READ_SESSION", "読取セッションの形式を確認してください。");
}
