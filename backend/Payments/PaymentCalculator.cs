using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Payments;

public static class PaymentCalculator
{
    public const string CalculationVersion = "thousand-remainder-v1";

    public static long ValidateTotal(long totalYen)
    {
        if (totalYen is < 1 or > 9_999_999)
            throw new ApiException(422, "VALIDATION_ERROR", "総額は1〜9,999,999円で指定してください。");
        return totalYen;
    }

    // Participants and loser come from the validated saved game, never from the request.
    // G08 sets RegisteredAt when committing the recalculated payment.
    public static PaymentSnapshot Calculate(long totalYen, IReadOnlyList<GameParticipant> participants, string loserParticipantId)
    {
        ValidateTotal(totalYen);
        if (participants.Count is < 2 or > 6 || participants.Any(p => p is null || string.IsNullOrEmpty(p.ParticipantId)) ||
            participants.Select(p => p.ParticipantId).Distinct(StringComparer.Ordinal).Count() != participants.Count ||
            !participants.Any(p => p.ParticipantId == loserParticipantId))
            throw new ArgumentException("A validated game with 2–6 distinct participants and one loser is required.", nameof(participants));

        var count = (long)participants.Count;
        var baseYen = totalYen / (count * 1000L) * 1000L;
        var lossYen = totalYen - baseYen * count;
        var loserPaymentYen = baseYen + lossYen;
        var shares = participants.Select(p => new PaymentShare
        {
            ParticipantId = p.ParticipantId,
            AmountYen = p.ParticipantId == loserParticipantId ? loserPaymentYen : baseYen
        }).ToArray();
        if (lossYen < 0 || lossYen >= count * 1000L || shares.Sum(s => s.AmountYen) != totalYen)
            throw new InvalidOperationException("Payment invariants failed.");
        return new()
        {
            TotalYen = totalYen, ParticipantCount = participants.Count, BaseYen = baseYen, LossYen = lossYen,
            LoserParticipantId = loserParticipantId, LoserPaymentYen = loserPaymentYen,
            CollectFromOthersYen = baseYen * (count - 1L), Shares = shares,
            CalculationVersion = CalculationVersion, RegisteredAt = null
        };
    }
}
