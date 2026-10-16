using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Games;

// Shared by update operations: carry the session forward and check successful commands before business state.
internal sealed class CommandStore(ICosmosPointReader store)
{
    internal async Task<DocumentRead<T>> Read<T>(string owner, string id, ReadSession session, CancellationToken token) where T : class
    {
        var read = await store.ReadAsync<T>(owner, id, session.Token, token);
        if (read.Document is not null && (string.IsNullOrEmpty(read.Etag) || string.IsNullOrEmpty(read.SessionToken))) throw CosmosErrors.Unavailable();
        if (!string.IsNullOrEmpty(read.SessionToken)) session.Accept(read.SessionToken);
        return read;
    }

    internal async Task<CommandDocument?> Find(string owner, Guid key, string hash, ReadSession session, CancellationToken token)
    {
        var command = (await Read<CommandDocument>(owner, $"command:{key:D}", session, token)).Document;
        if (command is null) return null;
        if (command.OwnerKey != owner || command.Id != $"command:{key:D}" || command.Type != "command" || command.SchemaVersion != 1 ||
            command.GameId == Guid.Empty || command.CreatedAt == default || command.CreatedAt.Offset != TimeSpan.Zero ||
            command.RequestHash is not { Length: 64 } || command.RequestHash.Any(c => !char.IsAsciiHexDigit(c))) throw CosmosErrors.Unavailable();
        if (command.RequestHash != hash) throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED", "冪等性キーは別の要求に使用されています。");
        return command;
    }
}
