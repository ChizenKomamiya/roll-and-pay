using System.Text.Json;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Games;

public sealed class GameCanceller(ICosmosPointReader store, ICosmosBatchWriter writer)
{
    private readonly CommandStore commands = new(store);

    public async Task<CommandResponse> CancelAsync(string owner, Guid id, Guid key, string hash, string ifMatch,
        ReadSession session, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await Execute(owner, id, key, hash, ifMatch, session, deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }

    private async Task<CommandResponse> Execute(string owner, Guid id, Guid key, string hash, string ifMatch,
        ReadSession session, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; attempt <= 3; attempt++)
            {
                var previous = await Replay(owner, id, key, hash, session, token);
                if (previous is not null) return previous;
                var saved = await commands.Read<GameDocument>(owner, $"game:{id:D}", session, token);
                var game = saved.Document;
                if (game is null || game.OwnerKey != owner) throw new ApiException(404, "GAME_NOT_FOUND", "ゲームが見つかりません。");
                if (!GameReadValidation.Header(game, id)) throw CosmosErrors.Unavailable();
                if (game.Status == "deleted") throw new ApiException(410, "GAME_DELETED", "削除済みのゲームです。");
                if (ifMatch != GameEtag.Create(id, game.Revision)) throw new ApiException(412, "VERSION_MISMATCH", "最新のゲームを取得してください。");
                if (!GameReadValidation.IsActive(game.Status)) throw new ApiException(409, "INVALID_GAME_STATE", "このゲームは中止できません。");
                if (game.CurrentRoundNumber < 1 || game.Revision == long.MaxValue) throw CosmosErrors.Unavailable();
                var round = await commands.Read<RoundDocument>(owner, $"round:{id:D}:{game.CurrentRoundNumber}", session, token);
                var control = await commands.Read<ControlDocument>(owner, "control", session, token);
                // A round or pointer read can straddle a concurrent game update. Reconfirm before validating them.
                var after = await commands.Read<GameDocument>(owner, game.Id, session, token);
                if (after.Document is null || saved.Etag != after.Etag)
                {
                    await Task.Delay(25, token);
                    continue;
                }
                if (after.Document.OwnerKey != owner || after.Document.Revision != game.Revision ||
                    !GameReadValidation.Snapshot(game, round.Document, owner) || control.Document is null ||
                    !GameReadValidation.Control(control.Document, owner) || control.Document.ActiveGameId != id)
                    throw CosmosErrors.Unavailable();
                if (attempt == 3) throw CosmosErrors.Unavailable();
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (now < game.UpdatedAt) throw CosmosErrors.Unavailable();
                var cancelled = game with { Status = "cancelled", Revision = game.Revision + 1, UpdatedAt = now, CancelledAt = now, Payment = null };
                var command = new CommandDocument
                {
                    Id = $"command:{key:D}", OwnerKey = owner, Type = "command", SchemaVersion = 1, GameId = id,
                    Operation = "cancelGame", RequestHash = hash, ResponseStatus = 200, ResponseEtag = GameEtag.Create(id, cancelled.Revision),
                    ResponseLocation = null, CreatedAt = now,
                    ResponseBody = JsonSerializer.SerializeToElement(GameResponse.From(cancelled, round.Document!),
                        CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions)
                };
                try
                {
                    var result = await writer.WriteAsync(owner,
                        [new(game.Id, cancelled, saved.Etag), new("control", control.Document with { ActiveGameId = null, UpdatedAt = now }, control.Etag),
                         new(command.Id, command)], session.Token, token);
                    if (!string.IsNullOrEmpty(result.SessionToken)) session.Accept(result.SessionToken);
                    if (!result.Conflict)
                    {
                        if (string.IsNullOrEmpty(result.SessionToken)) throw CosmosErrors.Unavailable();
                        return new(command, false);
                    }
                }
                catch (ApiException e) when (e.StatusCode == 503)
                {
                    previous = await Replay(owner, id, key, hash, session, token);
                    if (previous is not null) return previous;
                    throw;
                }
                await Task.Delay(25, token);
            }
            throw CosmosErrors.Unavailable();
        }
        catch (ApiException e) when ((e.StatusCode is 404 or 410 or 412 or 409) && e.Code != "IDEMPOTENCY_KEY_REUSED")
        {
            // Another invocation of the same key can commit between the initial command read and the game read.
            var previous = await Replay(owner, id, key, hash, session, token);
            if (previous is not null) return previous;
            throw;
        }
    }

    private async Task<CommandResponse?> Replay(string owner, Guid id, Guid key, string hash, ReadSession session, CancellationToken token)
    {
        var command = await commands.Find(owner, key, hash, session, token);
        if (command is null) return null;
        if (command.GameId != id || command.Operation != "cancelGame" || command.ResponseStatus != 200 || command.ResponseLocation is not null ||
            command.ResponseBody.ValueKind != JsonValueKind.Object || !command.ResponseBody.TryGetProperty("revision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var version) || version < 2 || command.ResponseEtag != GameEtag.Create(id, version))
            throw CosmosErrors.Unavailable();
        return new(command, true);
    }
}
