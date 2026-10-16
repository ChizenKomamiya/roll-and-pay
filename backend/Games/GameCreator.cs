using System.Text.Json;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Games;

public sealed record CommandResponse(CommandDocument Command, bool Replayed);

public sealed class GameCreator(ICosmosPointReader store, ICosmosBatchWriter writer, GameReader reader)
{
    private readonly CommandStore commands = new(store);
    public async Task<CommandResponse> CreateAsync(string owner, Guid key, string hash, string[] ids, ReadSession session, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        try
        {
            for (var attempt = 0; attempt <= 3; attempt++)
            {
                var previous = await Replay(owner, key, hash, session, token);
                if (previous is not null) return previous;
                var roster = await commands.Read<RosterDocument>(owner, "roster", session, token);
                if (!RosterValidation.IsValid(roster.Document, owner)) throw CosmosErrors.Unavailable();
                var control = await commands.Read<ControlDocument>(owner, "control", session, token);
                if (control.Document is null || !GameReadValidation.Control(control.Document, owner)) throw CosmosErrors.Unavailable();
                if (control.Document.ActiveGameId is not null)
                {
                    // Reconfirm a stable pointer and a valid active snapshot, including round integrity.
                    var active = await reader.GetActiveAsync(owner, session, token);
                    previous = await Replay(owner, key, hash, session, token);
                    if (previous is not null) return previous;
                    if (active is null) continue;
                    throw new ApiException(409, "ACTIVE_GAME_EXISTS", "未完了のゲームがあります。", details:
                        [new("gameId", "ACTIVE_GAME_EXISTS", active.GameId.ToString("D"))]);
                }
                var selected = ids.Select(id => roster.Document!.Participants.SingleOrDefault(p => p.ParticipantId == id && p.IsActive)).ToArray();
                if (selected.Any(p => p is null)) throw InvalidParticipants();
                if (attempt == 3) throw CosmosErrors.Unavailable();
                var id = Guid.NewGuid();
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var game = new GameDocument
                {
                    Id = $"game:{id:D}", OwnerKey = owner, Type = "game", SchemaVersion = 1, GameId = id, Revision = 1,
                    Status = "rolling", Participants = selected.Select((p, i) => new GameParticipant
                        { ParticipantId = p!.ParticipantId, DisplayName = p.DisplayName, Order = i + 1 }).ToArray(),
                    CurrentRoundNumber = 1, LoserParticipantId = null, Payment = null,
                    CreatedAt = now, UpdatedAt = now, CompletedAt = null, CancelledAt = null, DeletedAt = null
                };
                var round = new RoundDocument
                {
                    Id = $"round:{id:D}:1", OwnerKey = owner, Type = "round", SchemaVersion = 1, GameId = id, RoundNumber = 1,
                    Status = "open", Rolls = ids.Select(p => new RollSnapshot { ParticipantId = p, Value = null, RolledAt = null }).ToArray(),
                    MinimumValue = null, TiedParticipantIds = [], CreatedAt = now, CompletedAt = null
                };
                var command = new CommandDocument
                {
                    Id = $"command:{key:D}", OwnerKey = owner, Type = "command", SchemaVersion = 1, GameId = id,
                    Operation = "createGame", RequestHash = hash, ResponseStatus = 201, ResponseEtag = GameEtag.Create(id, 1),
                    ResponseLocation = $"/api/v1/games/{id:D}", CreatedAt = now,
                    ResponseBody = JsonSerializer.SerializeToElement(GameResponse.From(game, round), CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions)
                };
                var nextControl = control.Document with { ActiveGameId = id, UpdatedAt = now };
                try
                {
                    var result = await writer.WriteAsync(owner,
                        [new(game.Id, game), new(round.Id, round), new("control", nextControl, control.Etag),
                         new("roster", roster.Document!, roster.Etag), new(command.Id, command)], session.Token, token);
                    if (!string.IsNullOrEmpty(result.SessionToken)) session.Accept(result.SessionToken);
                    if (!result.Conflict)
                    {
                        if (string.IsNullOrEmpty(result.SessionToken)) throw CosmosErrors.Unavailable();
                        return new(command, false);
                    }
                }
                catch (ApiException e) when (e.StatusCode == 503)
                {
                    // The batch may have committed before the connection failed. Never submit a new operation here.
                    previous = await Replay(owner, key, hash, session, token);
                    if (previous is not null) return previous;
                    throw;
                }
                await Task.Delay(25, token);
            }
            throw CosmosErrors.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }

    private async Task<CommandResponse?> Replay(string owner, Guid key, string hash, ReadSession session, CancellationToken token)
    {
        var command = await commands.Find(owner, key, hash, session, token);
        if (command is null) return null;
        if (command.Operation != "createGame" || command.ResponseStatus != 201 || command.ResponseEtag != GameEtag.Create(command.GameId, 1) ||
            command.ResponseLocation != $"/api/v1/games/{command.GameId:D}" || command.ResponseBody.ValueKind != JsonValueKind.Object)
            throw CosmosErrors.Unavailable();
        return new(command, true);
    }

    public static ApiException InvalidParticipants() => new(422, "VALIDATION_ERROR", "参加者を2〜6名、重複なく選択してください。",
        details: [new("participantIds", "INVALID_PARTICIPANTS")]);
}
