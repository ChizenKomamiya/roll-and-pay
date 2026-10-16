using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Games;

public sealed class GameReader(ICosmosPointReader store)
{
    public const int MaxAttempts = 3;

    public Task<GameResponse> GetAsync(string owner, Guid id, ReadSession session, CancellationToken cancellationToken) =>
        WithDeadline(async token =>
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var snapshot = await ReadSnapshot(owner, id, session, token);
                if (!snapshot.Changed)
                    return snapshot.Game ?? throw new ApiException(snapshot.Deleted ? 410 : 404,
                        snapshot.Deleted ? "GAME_DELETED" : "GAME_NOT_FOUND", snapshot.Deleted ? "削除済みのゲームです。" : "ゲームが見つかりません。");
                await Task.Delay(25, token);
            }
            throw CosmosErrors.Unavailable();
        }, cancellationToken);

    public Task<GameResponse?> GetActiveAsync(string owner, ReadSession session, CancellationToken cancellationToken) =>
        WithDeadline<GameResponse?>(async token =>
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var before = await ReadControl(owner, session, token);
                var snapshot = before.Document!.ActiveGameId is { } id
                    ? await ReadSnapshot(owner, id, session, token) : new Snapshot(null, false, false);
                var after = await ReadControl(owner, session, token);
                if (before.Etag != after.Etag || before.Document.ActiveGameId != after.Document!.ActiveGameId || snapshot.Changed)
                {
                    await Task.Delay(25, token);
                    continue;
                }
                if (before.Document.ActiveGameId is null) return null;
                if (snapshot.Game is null || !GameReadValidation.IsActive(snapshot.Game.Status)) throw CosmosErrors.Unavailable();
                return snapshot.Game;
            }
            throw CosmosErrors.Unavailable();
        }, cancellationToken);

    private sealed record Snapshot(GameResponse? Game, bool Deleted, bool Changed);

    private async Task<Snapshot> ReadSnapshot(string owner, Guid id, ReadSession session, CancellationToken token)
    {
        var before = await Read<GameDocument>(owner, $"game:{id:D}", session, token);
        var game = before.Document;
        if (game is null || game.OwnerKey != owner) return new(null, false, false);
        if (!GameReadValidation.Header(game, id)) throw CosmosErrors.Unavailable();
        if (game.Status == "deleted") return new(null, true, false);
        if (game.CurrentRoundNumber < 1) throw CosmosErrors.Unavailable();
        var round = await Read<RoundDocument>(owner, $"round:{id:D}:{game.CurrentRoundNumber}", session, token);
        var after = await Read<GameDocument>(owner, $"game:{id:D}", session, token);
        // Even a mismatching round may be an intermediate read. Reconfirm the game before validating the pair.
        if (after.Document is null || before.Etag != after.Etag) return new(null, false, true);
        if (!GameReadValidation.Header(after.Document, id) || after.Document.OwnerKey != owner || after.Document.Revision != game.Revision ||
            !GameReadValidation.Snapshot(game, round.Document, owner)) throw CosmosErrors.Unavailable();
        return new(GameResponse.From(game, round.Document!), false, false);
    }

    private async Task<DocumentRead<ControlDocument>> ReadControl(string owner, ReadSession session, CancellationToken token)
    {
        var control = await Read<ControlDocument>(owner, "control", session, token);
        if (control.Document is null || !GameReadValidation.Control(control.Document, owner)) throw CosmosErrors.Unavailable();
        return control;
    }

    private async Task<DocumentRead<T>> Read<T>(string owner, string id, ReadSession session, CancellationToken token) where T : class
    {
        var read = await store.ReadAsync<T>(owner, id, session.Token, token);
        if (read.Document is not null && (string.IsNullOrEmpty(read.Etag) || string.IsNullOrEmpty(read.SessionToken))) throw CosmosErrors.Unavailable();
        if (!string.IsNullOrEmpty(read.SessionToken)) session.Accept(read.SessionToken);
        return read;
    }

    private static async Task<T> WithDeadline<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await read(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }
}
