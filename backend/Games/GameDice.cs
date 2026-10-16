using System.Security.Cryptography;
using System.Text.Json;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Games;

public interface IDie { int Roll(); }
public sealed class SecureDie : IDie { public int Roll() => RandomNumberGenerator.GetInt32(1, 7); }

public sealed record DiceRequest(int RoundNumber, string Mode, string? ParticipantId = null);

public sealed class GameDice(ICosmosPointReader store, ICosmosBatchWriter writer, IDie die)
{
    public async Task<CommandResponse> ExecuteAsync(string owner, Guid id, Guid key, string hash, string ifMatch,
        DiceRequest request, bool nextRound, ReadSession session, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var replay = await Replay(owner, id, key, hash, nextRound, session, token);
                if (replay is not null) return replay;
                var read = await Read<GameDocument>(owner, $"game:{id:D}", session, token);
                // A concurrent request may have committed after the first command lookup.
                replay = await Replay(owner, id, key, hash, nextRound, session, token);
                if (replay is not null) return replay;
                var game = read.Document;
                if (game is null || game.OwnerKey != owner) throw new ApiException(404, "GAME_NOT_FOUND", "ゲームが見つかりません。");
                if (!GameReadValidation.Header(game, id)) throw CosmosErrors.Unavailable();
                if (game.Status == "deleted") throw new ApiException(410, "GAME_DELETED", "削除済みのゲームです。");
                if (ifMatch != GameEtag.Create(id, game.Revision)) throw new ApiException(412, "VERSION_MISMATCH", "最新のゲームを取得してください。");
                var roundRead = await Read<RoundDocument>(owner, $"round:{id:D}:{game.CurrentRoundNumber}", session, token);
                var after = await Read<GameDocument>(owner, game.Id, session, token);
                if (after.Etag != read.Etag) { await Task.Delay(25, token); continue; }
                if (!GameReadValidation.Snapshot(game, roundRead.Document, owner)) throw CosmosErrors.Unavailable();
                var round = roundRead.Document!;
                if (game.Status != (nextRound ? "tie" : "rolling"))
                    throw new ApiException(409, "INVALID_GAME_STATE", "現在の状態では操作できません。");
                if (request.RoundNumber != game.CurrentRoundNumber)
                    throw new ApiException(409, "ROUND_MISMATCH", "現在のラウンドを指定してください。");
                if (game.Revision == long.MaxValue || (nextRound && game.CurrentRoundNumber == int.MaxValue)) throw CosmosErrors.Unavailable();
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (now < game.UpdatedAt) now = game.UpdatedAt;
                RoundDocument updatedRound;
                string status;
                string? loser = null;
                if (nextRound)
                {
                    var number = game.CurrentRoundNumber + 1;
                    updatedRound = new RoundDocument
                    {
                        Id = $"round:{id:D}:{number}", OwnerKey = owner, Type = "round", SchemaVersion = 1,
                        GameId = id, RoundNumber = number, Status = "open", CreatedAt = now, CompletedAt = null,
                        Rolls = game.Participants.Select(p => new RollSnapshot { ParticipantId = p.ParticipantId, Value = null, RolledAt = null }).ToArray(),
                        MinimumValue = null, TiedParticipantIds = []
                    };
                    status = "rolling";
                }
                else
                {
                    if (request.Mode == "individual")
                    {
                        var target = round.Rolls.SingleOrDefault(r => r.ParticipantId == request.ParticipantId);
                        if (target is null) throw new ApiException(422, "VALIDATION_ERROR", "ゲームの参加者を指定してください。", details: [new("participantId", "INVALID_PARTICIPANT")]);
                        if (target.Value is not null) throw new ApiException(409, "ALREADY_ROLLED", "抽選済みです。");
                    }
                    var rolls = round.Rolls.Select(r => r.Value is null && (request.Mode == "remaining" || r.ParticipantId == request.ParticipantId)
                        ? r with { Value = die.Roll(), RolledAt = now } : r).ToArray();
                    if (rolls.Any(r => r.Value is < 1 or > 6)) throw CosmosErrors.Unavailable();
                    var complete = rolls.All(r => r.Value is not null);
                    var minimum = complete ? rolls.Min(r => r.Value) : null;
                    var minima = complete ? rolls.Where(r => r.Value == minimum).Select(r => r.ParticipantId).ToArray() : [];
                    status = !complete ? "rolling" : minima.Length == 1 ? "awaitingPayment" : "tie";
                    loser = status == "awaitingPayment" ? minima[0] : null;
                    updatedRound = round with { Rolls = rolls, Status = !complete ? "open" : loser is null ? "tied" : "decided",
                        MinimumValue = minimum, TiedParticipantIds = status == "tie" ? minima : [], CompletedAt = complete ? now : null };
                }
                var updated = game with { Revision = game.Revision + 1, UpdatedAt = now, Status = status,
                    CurrentRoundNumber = updatedRound.RoundNumber, LoserParticipantId = loser };
                var command = new CommandDocument
                {
                    Id = $"command:{key:D}", OwnerKey = owner, Type = "command", SchemaVersion = 1, GameId = id,
                    Operation = nextRound ? "createRound" : "rollDice", RequestHash = hash, ResponseStatus = nextRound ? 201 : 200,
                    ResponseEtag = GameEtag.Create(id, updated.Revision), ResponseLocation = nextRound ? $"/api/v1/games/{id:D}" : null,
                    ResponseBody = JsonSerializer.SerializeToElement(GameResponse.From(updated, updatedRound), CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions), CreatedAt = now
                };
                try
                {
                    var result = await writer.WriteAsync(owner, [new(updatedRound.Id, updatedRound, nextRound ? null : roundRead.Etag),
                        new(updated.Id, updated, read.Etag), new(command.Id, command)], session.Token, token);
                    if (!string.IsNullOrEmpty(result.SessionToken)) session.Accept(result.SessionToken);
                    if (!result.Conflict)
                    {
                        if (string.IsNullOrEmpty(result.SessionToken)) throw CosmosErrors.Unavailable();
                        return new(command, false);
                    }
                }
                catch (ApiException e) when (e.StatusCode == 503)
                {
                    replay = await Replay(owner, id, key, hash, nextRound, session, token);
                    if (replay is not null) return replay;
                    throw;
                }
                // Retain the original public ETag: a competing update must not silently roll again.
                await Task.Delay(25, token);
            }
            // Also reconcile the last conditional failure before reporting an unavailable result.
            return await Replay(owner, id, key, hash, nextRound, session, token) ?? throw CosmosErrors.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }

    private async Task<CommandResponse?> Replay(string owner, Guid id, Guid key, string hash, bool nextRound, ReadSession session, CancellationToken token)
    {
        var command = (await Read<CommandDocument>(owner, $"command:{key:D}", session, token)).Document;
        if (command is null) return null;
        if (command.OwnerKey != owner || command.Id != $"command:{key:D}" || command.Type != "command" || command.SchemaVersion != 1 ||
            command.GameId == Guid.Empty || command.CreatedAt == default || command.CreatedAt.Offset != TimeSpan.Zero ||
            command.RequestHash is not { Length: 64 } || command.RequestHash.Any(c => !char.IsAsciiHexDigit(c))) throw CosmosErrors.Unavailable();
        if (command.RequestHash != hash) throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED", "冪等性キーは別の要求に使用されています。");
        if (command.GameId != id || command.Operation != (nextRound ? "createRound" : "rollDice") || command.ResponseStatus != (nextRound ? 201 : 200) ||
            command.ResponseLocation != (nextRound ? $"/api/v1/games/{id:D}" : null) || command.ResponseBody.ValueKind != JsonValueKind.Object ||
            !command.ResponseBody.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var version) || version < 2 ||
            command.ResponseEtag != GameEtag.Create(id, version)) throw CosmosErrors.Unavailable();
        return new(command, true);
    }

    private async Task<DocumentRead<T>> Read<T>(string owner, string id, ReadSession session, CancellationToken token) where T : class
    {
        var result = await store.ReadAsync<T>(owner, id, session.Token, token);
        if (result.Document is not null && (string.IsNullOrEmpty(result.Etag) || string.IsNullOrEmpty(result.SessionToken))) throw CosmosErrors.Unavailable();
        if (!string.IsNullOrEmpty(result.SessionToken)) session.Accept(result.SessionToken);
        return result;
    }
}
