using RollAndPay.Api.Payments;

namespace RollAndPay.Api.History;

public sealed record RemovedLoss(string ParticipantId, long AmountYen);
public sealed record HistoryDeleteResponse(Guid GameId, string Status, long Revision, DateTimeOffset DeletedAt,
    RemovedLoss RemovedLoss, LossTotalsResponse LossTotals);
