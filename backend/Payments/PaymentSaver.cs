using System.Text.Json;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using RollAndPay.Api.Games;

namespace RollAndPay.Api.Payments;

public sealed record PaymentSaveResponse(GameResponse Game, LossTotalsResponse LossTotals);

public sealed class PaymentSaver(ICosmosPointReader store, ICosmosBatchWriter writer)
{
    private readonly CommandStore commands = new(store);

    public async Task<CommandResponse> SaveAsync(string owner, Guid id, Guid key, string hash, string ifMatch, long totalYen,
        ReadSession session, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await Execute(owner, id, key, hash, ifMatch, PaymentCalculator.ValidateTotal(totalYen), session, deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }

    private async Task<CommandResponse> Execute(string owner, Guid id, Guid key, string hash, string ifMatch, long totalYen,
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
                if (game.Status != "awaitingPayment") throw new ApiException(409, "INVALID_GAME_STATE", "敗者確定後、金額確定前のゲームだけ保存できます。");
                if (game.CurrentRoundNumber < 1 || game.Revision == long.MaxValue) throw CosmosErrors.Unavailable();
                var round = await commands.Read<RoundDocument>(owner, $"round:{id:D}:{game.CurrentRoundNumber}", session, token);
                var control = await commands.Read<ControlDocument>(owner, "control", session, token);
                var roster = await commands.Read<RosterDocument>(owner, "roster", session, token);
                var totals = await commands.Read<LossTotalsDocument>(owner, "lossTotals", session, token);
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
                LossTotalsReader.Compose(roster.Document, totals.Document, owner);
                if (game.Participants.Any(p => !roster.Document!.Participants.Any(r => r.ParticipantId == p.ParticipantId)))
                    throw CosmosErrors.Unavailable();
                if (totals.Document!.UpdatedAt > now || control.Document.UpdatedAt > now) throw CosmosErrors.Unavailable();
                var payment = PaymentCalculator.Calculate(totalYen, game.Participants, game.LoserParticipantId!) with { RegisteredAt = now };
                LossTotal[] amounts;
                try
                {
                    amounts = totals.Document.Totals.Select(t => t.ParticipantId == game.LoserParticipantId
                        ? t with { TotalLossYen = LossTotalsReader.Format(checked(LossTotalsReader.Parse(t.TotalLossYen) + payment.LossYen)) } : t).ToArray();
                }
                catch (OverflowException) { throw CosmosErrors.Unavailable(); }
                var updatedTotals = totals.Document with { Totals = amounts, UpdatedAt = now };
                var completed = game with { Status = "completed", Revision = game.Revision + 1, UpdatedAt = now, CompletedAt = now, Payment = payment };
                var command = new CommandDocument
                {
                    Id = $"command:{key:D}", OwnerKey = owner, Type = "command", SchemaVersion = 1, GameId = id,
                    Operation = "savePayment", RequestHash = hash, ResponseStatus = 200, ResponseEtag = GameEtag.Create(id, completed.Revision),
                    ResponseLocation = null, CreatedAt = now,
                    ResponseBody = JsonSerializer.SerializeToElement(new PaymentSaveResponse(GameResponse.From(completed, round.Document!), LossTotalsReader.Compose(roster.Document, updatedTotals, owner)),
                        CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions)
                };
                try
                {
                    var result = await writer.WriteAsync(owner,
                        [new(game.Id, completed, saved.Etag), new("control", control.Document with { ActiveGameId = null, UpdatedAt = now }, control.Etag),
                         new("lossTotals", updatedTotals, totals.Etag), new(command.Id, command)], session.Token, token);
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
        if (command.GameId != id || command.Operation != "savePayment" || command.ResponseStatus != 200 || command.ResponseLocation is not null ||
            command.ResponseBody.ValueKind != JsonValueKind.Object || !command.ResponseBody.TryGetProperty("lossTotals", out var totals) || totals.ValueKind != JsonValueKind.Object ||
            !command.ResponseBody.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("revision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var version) || version < 2 || command.ResponseEtag != GameEtag.Create(id, version))
            throw CosmosErrors.Unavailable();
        return new(command, true);
    }
}
