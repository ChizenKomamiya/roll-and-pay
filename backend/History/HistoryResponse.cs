using RollAndPay.Api.Data;

namespace RollAndPay.Api.History;

public sealed record HistoryLoser(string ParticipantId, string DisplayName);
public sealed record HistoryItem(Guid GameId, DateTimeOffset CreatedAt, HistoryLoser Loser,
    int ParticipantCount, long TotalYen, long LossYen)
{
    public static HistoryItem From(GameDocument game)
    {
        var loser = game.Participants.Single(p => p.ParticipantId == game.LoserParticipantId);
        return new(game.GameId, game.CreatedAt, new(loser.ParticipantId, loser.DisplayName),
            game.Participants.Length, game.Payment!.TotalYen, game.Payment.LossYen);
    }
}

public sealed record HistoryListResponse(HistoryItem[] Items, string? NextCursor);
public sealed record HistoryDetailResponse(Guid GameId, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt, GameParticipant[] Participants, string LoserParticipantId,
    PaymentSnapshot Payment, bool CanDelete)
{
    public static HistoryDetailResponse From(GameDocument game) => new(game.GameId, game.Revision, game.CreatedAt,
        game.CompletedAt!.Value, game.Participants, game.LoserParticipantId!, game.Payment!, true);
}
