using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class PaymentCalculatorTests
{
    internal static GameParticipant[] Participants(int count) => Enumerable.Range(1, count).Select(i => new GameParticipant
        { ParticipantId = $"p{i:000}", DisplayName = $"参加者{i}", Order = i }).ToArray();

    [Theory]
    [InlineData(13800, 4, 3000, 1800, 4800, 9000)]
    [InlineData(4000, 4, 1000, 0, 1000, 3000)]
    [InlineData(3999, 4, 0, 3999, 3999, 0)]
    [InlineData(1, 2, 0, 1, 1, 0)]
    [InlineData(9999999, 6, 1666000, 3999, 1669999, 8330000)]
    public void Known_examples(long total, int count, long baseYen, long loss, long loserPayment, long collect)
    {
        var payment = PaymentCalculator.Calculate(total, Participants(count), "p002");
        Assert.Equal(total, payment.TotalYen);
        Assert.Equal(count, payment.ParticipantCount);
        Assert.Equal(baseYen, payment.BaseYen);
        Assert.Equal(loss, payment.LossYen);
        Assert.Equal(loserPayment, payment.LoserPaymentYen);
        Assert.Equal(collect, payment.CollectFromOthersYen);
        Assert.Equal("p002", payment.LoserParticipantId);
        Assert.Null(payment.RegisteredAt);
        Assert.Equal("thousand-remainder-v1", payment.CalculationVersion);
        Assert.Equal(Participants(count).Select(p => p.ParticipantId), payment.Shares.Select(p => p.ParticipantId));
        Assert.All(payment.Shares, share => Assert.Equal(share.ParticipantId == "p002" ? loserPayment : baseYen, share.AmountYen));
    }

    [Fact]
    public void Every_supported_count_and_loser_preserves_amounts_around_thousand_boundaries()
    {
        for (var count = 2; count <= 6; count++)
        foreach (var loser in Participants(count))
        foreach (var total in new long[] { 1, count * 1000 - 1, count * 1000, count * 1000 + 1, 9_999_999 })
        {
            var payment = PaymentCalculator.Calculate(total, Participants(count), loser.ParticipantId);
            Assert.Equal(total, payment.Shares.Sum(s => s.AmountYen));
            Assert.Equal(0, payment.BaseYen % 1000);
            Assert.InRange(payment.LossYen, 0, count * 1000 - 1);
            Assert.Equal(total, payment.CollectFromOthersYen + payment.LoserPaymentYen);
            Assert.Equal(payment.BaseYen * count + payment.LossYen, total);
        }
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10000000)]
    [InlineData(long.MaxValue)]
    public void Invalid_total_is_rejected(long total)
    {
        var error = Assert.Throws<ApiException>(() => PaymentCalculator.Calculate(total, Participants(4), "p001"));
        Assert.Equal(422, error.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Invalid_participant_count_is_rejected(int count) =>
        Assert.Throws<ArgumentException>(() => PaymentCalculator.Calculate(13800, Participants(count), "p001"));

    [Fact]
    public void Unknown_loser_and_duplicate_participants_cannot_be_calculated()
    {
        Assert.Throws<ArgumentException>(() => PaymentCalculator.Calculate(13800, Participants(4), "other"));
        var participant = Participants(1)[0];
        Assert.Throws<ArgumentException>(() => PaymentCalculator.Calculate(13800, [participant, participant], participant.ParticipantId));
    }
}
