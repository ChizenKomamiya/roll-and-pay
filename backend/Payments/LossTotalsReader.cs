using System.Globalization;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Payments;

public sealed record ParticipantLoss(string ParticipantId, string DisplayName, bool IsActive, string TotalLossYen);
public sealed record LossTotalsResponse(ParticipantLoss[] Participants, DateTimeOffset? UpdatedAt);

public sealed class LossTotalsReader(ICosmosPointReader store)
{
    private readonly CommandStore documents = new(store);

    public async Task<LossTotalsResponse> GetAsync(string owner, ReadSession session, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var roster = await documents.Read<RosterDocument>(owner, "roster", session, deadline.Token);
                var totals = await documents.Read<LossTotalsDocument>(owner, "lossTotals", session, deadline.Token);
                var after = await documents.Read<RosterDocument>(owner, "roster", session, deadline.Token);
                if (roster.Etag != after.Etag) { await Task.Delay(25, deadline.Token); continue; }
                return Compose(roster.Document, totals.Document, owner);
            }
            throw CosmosErrors.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
    }

    // Validate a complete one-to-one mapping, including inactive roster entries. Never silently drop totals.
    public static LossTotalsResponse Compose(RosterDocument? roster, LossTotalsDocument? totals, string owner)
    {
        if (!RosterValidation.IsValid(roster, owner) || totals is null || totals.Id != "lossTotals" ||
            totals.Type != "lossTotals" || totals.SchemaVersion != 1 || totals.OwnerKey != owner ||
            totals.UpdatedAt is { } time && (time == default || time.Offset != TimeSpan.Zero) ||
            totals.Totals is null || totals.Totals.Length != roster!.Participants.Length ||
            totals.Totals.Any(t => t is null || t.ParticipantId is null) ||
            totals.Totals.Select(t => t.ParticipantId).Distinct(StringComparer.Ordinal).Count() != totals.Totals.Length)
            throw CosmosErrors.Unavailable();
        var amounts = totals.Totals.ToDictionary(t => t.ParticipantId, t => Parse(t.TotalLossYen), StringComparer.Ordinal);
        if (totals.UpdatedAt is null && amounts.Values.Any(v => v != 0)) throw CosmosErrors.Unavailable();
        return new(roster!.Participants.OrderBy(p => p.SortOrder).ThenBy(p => p.ParticipantId, StringComparer.Ordinal)
            .Select(p => new ParticipantLoss(p.ParticipantId, p.DisplayName, p.IsActive,
                amounts.TryGetValue(p.ParticipantId, out var amount) ? Format(amount) : throw CosmosErrors.Unavailable())).ToArray(), totals.UpdatedAt);
    }

    public static long Parse(string? amount)
    {
        if (string.IsNullOrEmpty(amount) || amount.Length > 19 || (amount.Length > 1 && amount[0] == '0') ||
            amount.Any(c => c is < '0' or > '9') || !long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw CosmosErrors.Unavailable();
        return value;
    }

    public static string Format(long amount) => amount.ToString(CultureInfo.InvariantCulture);
}
