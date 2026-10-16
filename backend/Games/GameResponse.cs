using System.Globalization;
using RollAndPay.Api.Data;

namespace RollAndPay.Api.Games;

public sealed record RoundResponse(int RoundNumber, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, RollSnapshot[] Rolls, int? MinimumValue, string[] TiedParticipantIds);
public sealed record GameResponse(Guid GameId, long Revision, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancelledAt,
    GameParticipant[] Participants, RoundResponse CurrentRound, string? LoserParticipantId, PaymentSnapshot? Payment)
{
    public static GameResponse From(GameDocument game, RoundDocument round) => new(game.GameId, game.Revision,
        game.Status, game.CreatedAt, game.UpdatedAt, game.CompletedAt, game.CancelledAt, game.Participants,
        new(round.RoundNumber, round.Status, round.CreatedAt, round.CompletedAt, round.Rolls, round.MinimumValue, round.TiedParticipantIds),
        game.LoserParticipantId, game.Payment);
}

public static class GameEtag
{
    public static string Create(Guid gameId, long revision) => $"\"g-{gameId:D}-v{revision.ToString(CultureInfo.InvariantCulture)}\"";
}
