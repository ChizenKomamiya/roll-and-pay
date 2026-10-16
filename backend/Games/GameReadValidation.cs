using RollAndPay.Api.Data;

namespace RollAndPay.Api.Games;

public static class GameReadValidation
{
    public static bool IsActive(string status) => status is "rolling" or "tie" or "awaitingPayment";
    private static bool Utc(DateTimeOffset time) => time != default && time.Offset == TimeSpan.Zero;
    private static bool Between(DateTimeOffset? time, DateTimeOffset start, DateTimeOffset end) =>
        time is { } value && Utc(value) && value >= start && value <= end;

    public static bool Control(ControlDocument control, string owner) => control.Id == "control" && control.Type == "control" &&
        control.OwnerKey == owner && control.SchemaVersion == 1 && Utc(control.UpdatedAt) && control.ActiveGameId != Guid.Empty;

    public static bool Header(GameDocument game, Guid id) => game.Id == $"game:{id:D}" && game.Type == "game" &&
        game.SchemaVersion == 1 && game.GameId == id && game.Revision > 0 &&
        (IsActive(game.Status) || game.Status is "completed" or "cancelled" or "deleted");

    public static bool Snapshot(GameDocument game, RoundDocument? round, string owner)
    {
        if (round is null || !Utc(game.CreatedAt) || !Utc(game.UpdatedAt) || game.UpdatedAt < game.CreatedAt ||
            game.DeletedAt is not null || game.CurrentRoundNumber < 1 || game.Participants is not { Length: >= 2 and <= 6 } ||
            game.Participants.Any(p => p is null) ||
            !RosterValidation.IsValid(game.Participants.Select(p => new RosterParticipant
                { ParticipantId = p.ParticipantId, DisplayName = p.DisplayName, SortOrder = p.Order, IsActive = true }).ToArray()) ||
            game.Participants.Where((p, i) => p.Order != i + 1).Any()) return false;

        if (round.Id != $"round:{game.GameId:D}:{game.CurrentRoundNumber}" || round.Type != "round" || round.SchemaVersion != 1 ||
            round.OwnerKey != owner || round.GameId != game.GameId || round.RoundNumber != game.CurrentRoundNumber ||
            !Between(round.CreatedAt, game.CreatedAt, game.UpdatedAt) || round.Rolls is null || round.TiedParticipantIds is null ||
            round.Rolls.Length != game.Participants.Length || round.Rolls.Any(r => r is null) ||
            !round.Rolls.Select(r => r.ParticipantId).SequenceEqual(game.Participants.Select(p => p.ParticipantId))) return false;

        foreach (var roll in round.Rolls)
        {
            if (roll.Value is null ? roll.RolledAt is not null : roll.Value is < 1 or > 6 ||
                !Between(roll.RolledAt, round.CreatedAt, round.CompletedAt ?? game.UpdatedAt)) return false;
        }

        string? expectedLoser;
        if (round.Status == "open")
        {
            if (round.Rolls.All(r => r.Value is not null) || round.CompletedAt is not null ||
                round.MinimumValue is not null || round.TiedParticipantIds.Length != 0 ||
                game.Status is not ("rolling" or "cancelled")) return false;
            expectedLoser = null;
        }
        else
        {
            if (round.Rolls.Any(r => r.Value is null) || !Between(round.CompletedAt, round.CreatedAt, game.UpdatedAt)) return false;
            var minimum = round.Rolls.Min(r => r.Value!.Value);
            var minima = round.Rolls.Where(r => r.Value == minimum).Select(r => r.ParticipantId).ToArray();
            if (round.MinimumValue != minimum) return false;
            if (round.Status == "tied")
            {
                if (minima.Length < 2 || !round.TiedParticipantIds.SequenceEqual(minima) || game.Status is not ("tie" or "cancelled")) return false;
                expectedLoser = null;
            }
            else if (round.Status == "decided")
            {
                if (minima.Length != 1 || round.TiedParticipantIds.Length != 0 || game.Status is not ("awaitingPayment" or "completed" or "cancelled")) return false;
                expectedLoser = minima[0];
            }
            else return false;
        }
        if (game.LoserParticipantId != expectedLoser) return false;

        if (game.Status == "completed")
            return game.CancelledAt is null && Between(game.CompletedAt, round.CompletedAt!.Value, game.UpdatedAt) && Payment(game);
        if (game.CompletedAt is not null || game.Payment is not null) return false;
        return game.Status == "cancelled"
            ? Between(game.CancelledAt, round.CompletedAt ?? round.CreatedAt, game.UpdatedAt) && round.Rolls.All(r => r.RolledAt is null || r.RolledAt <= game.CancelledAt)
            : game.CancelledAt is null;
    }

    private static bool Payment(GameDocument game)
    {
        var payment = game.Payment;
        if (payment is null || payment.TotalYen is < 1 or > 9_999_999 || payment.ParticipantCount != game.Participants.Length ||
            payment.BaseYen < 0 || payment.BaseYen > payment.TotalYen || payment.LossYen < 0 || payment.LossYen > payment.TotalYen ||
            payment.LoserParticipantId != game.LoserParticipantId || payment.RegisteredAt != game.CompletedAt ||
            payment.RegisteredAt is null || !Utc(payment.RegisteredAt.Value) || string.IsNullOrWhiteSpace(payment.CalculationVersion) ||
            payment.Shares is null || payment.Shares.Any(s => s is null) ||
            !payment.Shares.Select(s => s.ParticipantId).SequenceEqual(game.Participants.Select(p => p.ParticipantId))) return false;
        // Validate saved amounts without applying today's calculation policy to historical payments.
        return payment.LoserPaymentYen == payment.BaseYen + payment.LossYen &&
            payment.CollectFromOthersYen == payment.BaseYen * (payment.ParticipantCount - 1) &&
            payment.TotalYen == payment.LoserPaymentYen + payment.CollectFromOthersYen &&
            payment.Shares.All(s => s.AmountYen == (s.ParticipantId == game.LoserParticipantId ? payment.LoserPaymentYen : payment.BaseYen));
    }
}
