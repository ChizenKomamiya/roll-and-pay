using System.Text.Json;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;

namespace RollAndPay.Api.History;

public sealed class HistoryDeleter(ICosmosPointReader store, ICosmosBatchWriter writer)
{
    private readonly CommandStore commands = new(store);

    public async Task<CommandResponse> DeleteAsync(string owner, Guid id, Guid key, string hash, string ifMatch,
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
                if (ifMatch != GameEtag.Create(id, game.Revision)) throw new ApiException(412, "VERSION_MISMATCH", "最新の履歴を取得してください。");
                if (game.Status != "completed") throw new ApiException(409, "INVALID_GAME_STATE", "完了済みの履歴だけ削除できます。");
                if (!GameReadValidation.Completed(game, owner) || game.Revision == long.MaxValue) throw CosmosErrors.Unavailable();
                if (attempt == 3) throw CosmosErrors.Unavailable();
                var roster = await commands.Read<RosterDocument>(owner, "roster", session, token);
                var totals = await commands.Read<LossTotalsDocument>(owner, "lossTotals", session, token);
                LossTotalsReader.Compose(roster.Document, totals.Document, owner);
                if (game.Participants.Any(p => !roster.Document!.Participants.Any(r => r.ParticipantId == p.ParticipantId)))
                    throw CosmosErrors.Unavailable();
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (now < game.UpdatedAt || totals.Document!.UpdatedAt > now) throw CosmosErrors.Unavailable();
                // Historical L is authoritative; never recalculate it with today's payment policy.
                var loss = game.Payment!.LossYen;
                var amount = LossTotalsReader.Parse(totals.Document.Totals.Single(t => t.ParticipantId == game.LoserParticipantId).TotalLossYen);
                if (amount < loss) throw CosmosErrors.Unavailable();
                var updatedTotals = totals.Document with
                {
                    Totals = totals.Document.Totals.Select(t => t.ParticipantId == game.LoserParticipantId
                        ? t with { TotalLossYen = LossTotalsReader.Format(checked(amount - loss)) } : t).ToArray(),
                    UpdatedAt = now
                };
                var deleted = game with { Status = "deleted", Revision = game.Revision + 1, UpdatedAt = now, DeletedAt = now };
                var command = new CommandDocument
                {
                    Id = $"command:{key:D}", OwnerKey = owner, Type = "command", SchemaVersion = 1, GameId = id,
                    Operation = "deleteHistory", RequestHash = hash, ResponseStatus = 200, ResponseEtag = GameEtag.Create(id, deleted.Revision),
                    ResponseLocation = null, CreatedAt = now,
                    ResponseBody = JsonSerializer.SerializeToElement(new HistoryDeleteResponse(id, deleted.Status, deleted.Revision, now,
                        new(game.LoserParticipantId!, loss), LossTotalsReader.Compose(roster.Document, updatedTotals, owner)),
                        CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions)
                };
                try
                {
                    // The active-game pointer and all rounds are deliberately outside this batch.
                    var result = await writer.WriteAsync(owner,
                        [new(game.Id, deleted, saved.Etag), new("lossTotals", updatedTotals, totals.Etag), new(command.Id, command)], session.Token, token);
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
            // A matching command may have committed after the first command read.
            var previous = await Replay(owner, id, key, hash, session, token);
            if (previous is not null) return previous;
            throw;
        }
    }

    private async Task<CommandResponse?> Replay(string owner, Guid id, Guid key, string hash, ReadSession session, CancellationToken token)
    {
        var command = await commands.Find(owner, key, hash, session, token);
        if (command is null) return null;
        var body = command.ResponseBody;
        if (command.GameId != id || command.Operation != "deleteHistory" || command.ResponseStatus != 200 || command.ResponseLocation is not null ||
            body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("gameId", out var gameId) || gameId.ValueKind != JsonValueKind.String ||
            !gameId.TryGetGuid(out var target) || target != id || !body.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "deleted" ||
            !body.TryGetProperty("lossTotals", out var totals) || totals.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var version) || version < 2 || command.ResponseEtag != GameEtag.Create(id, version))
            throw CosmosErrors.Unavailable();
        return new(command, true);
    }
}
