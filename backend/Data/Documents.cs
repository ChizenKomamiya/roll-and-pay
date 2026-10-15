namespace RollAndPay.Api.Data;

public sealed record RosterParticipant
{
    public required string ParticipantId { get; init; }
    public required string DisplayName { get; init; }
    public required int SortOrder { get; init; }
    public required bool IsActive { get; init; }
}

public sealed record RosterDocument
{
    public required string Id { get; init; }
    public required string OwnerKey { get; init; }
    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required RosterParticipant[] Participants { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ControlDocument
{
    public required string Id { get; init; }
    public required string OwnerKey { get; init; }
    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required Guid? ActiveGameId { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record LossTotal(string ParticipantId, string TotalLossYen);
public sealed record LossTotalsDocument
{
    public required string Id { get; init; }
    public required string OwnerKey { get; init; }
    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required LossTotal[] Totals { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
