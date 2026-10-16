using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.History;

public sealed class HistoryReader(IHistoryStore history, ICosmosPointReader points)
{
    public Task<HistoryListResponse> ListAsync(string owner, HistoryQuery query, ReadSession session, CancellationToken cancellationToken) =>
        WithDeadline(async token =>
        {
            var read = await history.QueryAsync(owner, query, session.Token, token);
            session.Accept(read.SessionToken);
            if (read.Games.Count > query.Limit + 1) throw CosmosErrors.Unavailable();
            var before = query.Boundary;
            foreach (var game in read.Games)
            {
                if (game is null || !GameReadValidation.Completed(game, owner) ||
                    game.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                    (before is not null && (game.CreatedAt > before.CreatedAt ||
                    (game.CreatedAt == before.CreatedAt && string.CompareOrdinal(game.GameId.ToString("D"), before.GameId.ToString("D")) >= 0))))
                    throw CosmosErrors.Unavailable();
                before = new(game.CreatedAt, game.GameId);
            }
            var items = read.Games.Take(query.Limit).Select(HistoryItem.From).ToArray();
            var last = items.LastOrDefault();
            var cursor = read.Games.Count > query.Limit && last is not null
                ? HistoryQuery.Encode(new(last.CreatedAt, last.GameId), query.Limit) : null;
            return new HistoryListResponse(items, cursor);
        }, cancellationToken);

    public Task<HistoryDetailResponse> DetailAsync(string owner, Guid id, ReadSession session, CancellationToken cancellationToken) =>
        WithDeadline(async token =>
        {
            var read = await points.ReadAsync<GameDocument>(owner, $"game:{id:D}", session.Token, token);
            var game = read.Document;
            if (!string.IsNullOrEmpty(read.SessionToken)) session.Accept(read.SessionToken);
            if (game is null || game.OwnerKey != owner) throw new ApiException(404, "GAME_NOT_FOUND", "ゲームが見つかりません。");
            if (string.IsNullOrEmpty(read.Etag) || string.IsNullOrEmpty(read.SessionToken) || !GameReadValidation.Header(game, id))
                throw CosmosErrors.Unavailable();
            if (game.Status == "deleted") throw new ApiException(410, "GAME_DELETED", "削除済みのゲームです。");
            if (game.Status != "completed") throw new ApiException(409, "INVALID_GAME_STATE", "完了済みのゲームを指定してください。");
            if (!GameReadValidation.Completed(game, owner)) throw CosmosErrors.Unavailable();
            return HistoryDetailResponse.From(game);
        }, cancellationToken);

    private static async Task<T> WithDeadline<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await read(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }
}
