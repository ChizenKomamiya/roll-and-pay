using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.History;

public sealed record HistoryBoundary(DateTimeOffset CreatedAt, Guid GameId);
public sealed record HistoryQuery(int Limit, HistoryBoundary? Boundary)
{
    public const int MaxCursorLength = 4 * 1024;
    public const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static HistoryQuery Parse(IQueryCollection query)
    {
        if (query.Keys.Any(key => key is not ("limit" or "cursor"))) throw Invalid();
        var limit = 20;
        if (query.TryGetValue("limit", out var limits) && (limits.Count != 1 ||
            !int.TryParse(limits[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 50)) throw Invalid();
        if (!query.TryGetValue("cursor", out var cursors)) return new(limit, null);
        if (cursors.Count != 1) throw InvalidCursor();
        return new(limit, Decode(cursors[0], limit));
    }

    public static string Encode(HistoryBoundary boundary, int limit) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1, createdAt = boundary.CreatedAt.ToString(DateFormat, CultureInfo.InvariantCulture),
        gameId = boundary.GameId.ToString("D"), limit
    }));

    private static HistoryBoundary Decode(string? cursor, int limit)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxCursorLength || cursor.Length % 4 == 1 ||
            cursor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw InvalidCursor();
        try
        {
            var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '='));
            if (Base64Url(bytes) != cursor) throw InvalidCursor();
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidCursor();
            var fields = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Length != 4 || fields.Distinct(StringComparer.Ordinal).Count() != 4 ||
                fields.Any(p => p is not ("version" or "createdAt" or "gameId" or "limit"))) throw InvalidCursor();
            if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                !root.GetProperty("limit").TryGetInt32(out var savedLimit) || savedLimit != limit) throw InvalidCursor();
            var date = root.GetProperty("createdAt").GetString();
            var id = root.GetProperty("gameId").GetString();
            if (!DateTimeOffset.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt) || createdAt == default ||
                id?.Length != 36 || !Guid.TryParseExact(id, "D", out var gameId) || gameId == Guid.Empty || gameId.ToString("D") != id) throw InvalidCursor();
            return new(createdAt, gameId);
        }
        catch (FormatException) { throw InvalidCursor(); }
        catch (JsonException) { throw InvalidCursor(); }
        catch (InvalidOperationException) { throw InvalidCursor(); }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static ApiException InvalidCursor() => new(400, "INVALID_CURSOR", "履歴カーソルの形式を確認してください。");
    public static ApiException Invalid() => new(400, "INVALID_REQUEST", "履歴の取得条件を確認してください。");
}
