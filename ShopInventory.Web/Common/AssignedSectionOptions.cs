namespace ShopInventory.Web.Common;

public static class AssignedSectionOptions
{
    private static readonly IReadOnlyDictionary<string, string> DisplayLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Cheeseman"] = "Cheeseman DC Harare",
        ["Factory"] = "Factory - Dispatch",
        ["Bulawayo"] = "Cheeseman DC Byo"
    };

    public static string[] All { get; } =
    [
        "Cheeseman",
        "Cheeseman DC Richwell",
        "Cheeseman DC Vic Falls",
        "Factory",
        "Graniteside",
        "Machipisa",
        "Bulawayo"
    ];

    public static string GetLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalizedValue = value.Trim();
        return DisplayLabels.TryGetValue(normalizedValue, out var label)
            ? label
            : normalizedValue;
    }

    /// <summary>
    /// Reduces a section to the option value it belongs to, so the several
    /// spellings the same place arrives under all compare equal.
    ///
    /// A user's assigned section is stored as the short option ("Cheeseman"),
    /// while an invoice's generated location carries the full name SAP records
    /// ("Cheeseman DC Harare"), and either can turn up spaced or hyphenated
    /// differently ("Factory-Dispatch" against "Factory - Dispatch"). Both are
    /// matched against the option list and its labels; anything unrecognised
    /// comes back trimmed but otherwise as given, so an unknown section still
    /// compares equal to itself rather than collapsing into the others.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmedValue = value.Trim();
        var comparableValue = Comparable(trimmedValue);

        foreach (var option in All)
        {
            if (string.Equals(Comparable(option), comparableValue, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Comparable(GetLabel(option)), comparableValue, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return trimmedValue;
    }

    /// <summary>Whether two section values name the same place.</summary>
    public static bool AreSameSection(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Comparable(string value) =>
        string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();
}