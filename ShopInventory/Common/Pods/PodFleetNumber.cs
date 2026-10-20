namespace ShopInventory.Common.Pods;

/// <summary>
/// The fleet number of the truck a driver delivered with, captured on each POD they upload.
/// </summary>
/// <remarks>
/// Asked of drivers working out of the four Cheeseman distribution centres only — Harare (stored as
/// the section "Cheeseman"), Richwell, Vic Falls and Bulawayo ("Cheeseman DC Byo"). Factory,
/// Graniteside and Machipisa drivers are not asked. The login reply carries the answer as
/// <c>RequiresFleetNumber</c>, so the mobile app never has to know which sections these are.
///
/// The upload routes accept a missing number rather than refusing it. Drafts queued offline by a
/// build that predates the field upload days later, and refusing them would strand a real delivery
/// with no way to attach its proof. The app is where the number is made mandatory.
/// </remarks>
public static class PodFleetNumber
{
    /// <summary>The column width. Fleet numbers are short codes; anything longer is a mistake.</summary>
    public const int MaxLength = 20;

    private static readonly HashSet<string> SectionsRequiringFleetNumber = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cheeseman",
        "Cheeseman DC Richwell",
        "Cheeseman DC Vic Falls",
        "Bulawayo"
    };

    public static bool IsRequiredFor(string? role, string? assignedSection) =>
        string.Equals(role?.Trim(), "Driver", StringComparison.OrdinalIgnoreCase) &&
        SectionsRequiringFleetNumber.Contains(PodLocationScope.CanonicalizeSection(assignedSection));

    /// <summary>
    /// Trimmed and upper-cased so "ch 12" and "CH 12 " are stored as one truck; null when blank.
    /// </summary>
    public static string? Normalize(string? fleetNumber)
    {
        if (string.IsNullOrWhiteSpace(fleetNumber))
        {
            return null;
        }

        return string.Join(' ', fleetNumber.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    public static bool IsTooLong(string? normalizedFleetNumber) =>
        normalizedFleetNumber is not null && normalizedFleetNumber.Length > MaxLength;
}
