using System.Text.RegularExpressions;

namespace RollAndPay.Api.Data;

public static partial class RosterValidation
{
    public static bool IsValid(RosterParticipant[]? participants) => participants is { Length: <= 100 } &&
        participants.All(p => p is not null && p.ParticipantId is not null && IdPattern().IsMatch(p.ParticipantId) &&
            !string.IsNullOrWhiteSpace(p.DisplayName) && p.DisplayName.Length <= 50 &&
            p.SortOrder >= 0) &&
        participants.Select(p => p.ParticipantId).Distinct(StringComparer.Ordinal).Count() == participants.Length;

    public static bool IsValid(RosterDocument? roster, string ownerKey) => roster is not null &&
        roster.Id == "roster" && roster.Type == "roster" && roster.SchemaVersion == 1 &&
        roster.OwnerKey == ownerKey && roster.UpdatedAt != default && roster.UpdatedAt.Offset == TimeSpan.Zero &&
        IsValid(roster.Participants);

    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
