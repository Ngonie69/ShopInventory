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
}