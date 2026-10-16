namespace RollAndPay.Api.Data;

public sealed record GameParticipant
{
    public required string ParticipantId { get; init; }
    public required string DisplayName { get; init; }
    public required int Order { get; init; }
}

public sealed record PaymentShare
{
    public required string ParticipantId { get; init; }
    public required long AmountYen { get; init; }
}

public sealed record PaymentSnapshot
{
    public required long TotalYen { get; init; }
    public required int ParticipantCount { get; init; }
    public required long BaseYen { get; init; }
    public required long LossYen { get; init; }
    public required string LoserParticipantId { get; init; }
    public required long LoserPaymentYen { get; init; }
    public required long CollectFromOthersYen { get; init; }
    public required PaymentShare[] Shares { get; init; }
    public required string CalculationVersion { get; init; }
    public required DateTimeOffset? RegisteredAt { get; init; }
}

public sealed record GameDocument
{
    public required string Id { get; init; }
    public required string OwnerKey { get; init; }
    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required Guid GameId { get; init; }
    public required long Revision { get; init; }
    public required string Status { get; init; }
    public required GameParticipant[] Participants { get; init; }
    public required int CurrentRoundNumber { get; init; }
    public required string? LoserParticipantId { get; init; }
    public required PaymentSnapshot? Payment { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset? CancelledAt { get; init; }
    // Compatible addition to schemaVersion 1: older non-deleted documents omit this property.
    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record RollSnapshot
{
    public required string ParticipantId { get; init; }
    public required int? Value { get; init; }
    public required DateTimeOffset? RolledAt { get; init; }
}

public sealed record RoundDocument
{
    public required string Id { get; init; }
    public required string OwnerKey { get; init; }
    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required Guid GameId { get; init; }
    public required int RoundNumber { get; init; }
    public required string Status { get; init; }
    public required RollSnapshot[] Rolls { get; init; }
    public required int? MinimumValue { get; init; }
    public required string[] TiedParticipantIds { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset? CompletedAt { get; init; }
}
