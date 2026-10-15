using RollAndPay.Api.Data;

namespace RollAndPay.Seed;

public sealed record InitialData(RosterDocument Roster, ControlDocument Control, LossTotalsDocument LossTotals)
{
    public static InitialData Build(string ownerKey, RosterParticipant[] participants, DateTimeOffset now)
    {
        if (!RosterValidation.IsValid(participants) || now == default || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid participant data or timestamp.");
        var ids = ownerKey.Split(':');
        if (ids.Length != 2 || !Guid.TryParse(ids[0], out var tenant) || !Guid.TryParse(ids[1], out var user) ||
            OwnerKey.FromIds(tenant, user) != ownerKey) throw new ArgumentException("Invalid owner key.");

        return new(
            new() { Id = "roster", OwnerKey = ownerKey, Type = "roster", SchemaVersion = 1,
                Participants = participants.ToArray(), UpdatedAt = now },
            new() { Id = "control", OwnerKey = ownerKey, Type = "control", SchemaVersion = 1,
                ActiveGameId = null, UpdatedAt = now },
            new() { Id = "lossTotals", OwnerKey = ownerKey, Type = "lossTotals", SchemaVersion = 1,
                Totals = participants.Select(p => new LossTotal(p.ParticipantId, "0")).ToArray(), UpdatedAt = now });
    }
}
