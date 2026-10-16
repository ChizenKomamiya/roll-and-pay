using RollAndPay.Api.Data;

namespace RollAndPay.Api.Tests;

internal static class GameFixture
{
    internal static readonly Guid Id = Guid.Parse("7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01");
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T00:00:00Z");
    internal static ControlDocument Control(Guid? active = null) => new()
    {
        Id = "control", Type = "control", OwnerKey = DataTests.Owner, SchemaVersion = 1,
        ActiveGameId = active, UpdatedAt = Now
    };
    internal static GameDocument Game(string status = "rolling") => new()
    {
        Id = $"game:{Id:D}", Type = "game", OwnerKey = DataTests.Owner, SchemaVersion = 1, GameId = Id,
        Revision = 4, Status = status, CurrentRoundNumber = 1, CreatedAt = Now, UpdatedAt = Now.AddMinutes(3),
        CompletedAt = status is "completed" or "deleted" ? Now.AddMinutes(2) : null,
        CancelledAt = status == "cancelled" ? Now.AddMinutes(2) : null,
        DeletedAt = status == "deleted" ? Now.AddMinutes(3) : null,
        Participants = [new() { ParticipantId = "p001", DisplayName = "当時の名前A", Order = 1 }, new() { ParticipantId = "p002", DisplayName = "当時の名前B", Order = 2 }],
        LoserParticipantId = status is "awaitingPayment" or "completed" or "deleted" ? "p001" : null,
        Payment = status is "completed" or "deleted" ? new()
        {
            TotalYen = 3800, ParticipantCount = 2, BaseYen = 1000, LossYen = 1800, LoserParticipantId = "p001",
            LoserPaymentYen = 2800, CollectFromOthersYen = 1000, CalculationVersion = "thousand-remainder-v1", RegisteredAt = Now.AddMinutes(2),
            Shares = [new() { ParticipantId = "p001", AmountYen = 2800 }, new() { ParticipantId = "p002", AmountYen = 1000 }]
        } : null
    };
    internal static RoundDocument Round(string status = "rolling")
    {
        var tied = status == "tie";
        var decided = status is "awaitingPayment" or "completed";
        return new()
        {
            Id = $"round:{Id:D}:1", OwnerKey = DataTests.Owner, Type = "round", SchemaVersion = 1,
            GameId = Id, RoundNumber = 1, Status = tied ? "tied" : decided ? "decided" : "open",
            CreatedAt = Now, CompletedAt = tied || decided ? Now.AddMinutes(1) : null,
            Rolls = [new() { ParticipantId = "p001", Value = 1, RolledAt = Now.AddSeconds(30) },
                new() { ParticipantId = "p002", Value = tied ? 1 : decided ? 6 : null, RolledAt = tied || decided ? Now.AddSeconds(45) : null }],
            MinimumValue = tied || decided ? 1 : null, TiedParticipantIds = tied ? ["p001", "p002"] : []
        };
    }
}
