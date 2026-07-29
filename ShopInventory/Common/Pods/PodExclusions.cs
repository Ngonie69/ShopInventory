namespace ShopInventory.Common.Pods;

public static class PodExclusions
{
    public static readonly HashSet<string> ExcludedCardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CIS006", "MAC009", "MAC006", "COR007", "COR006", "COR008", "COR011",
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013",
        "VAN014", "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020",
        "STA040", "STA041", "STA042", "STA043", "STA044", "STA045", "STA046", "STA047", "STA048",
        "PRO030", "PRO031", "PRO032", "PRO033", "PRO034", "PRO035", "PRO036",
        "CAS004(FCA)", "DON004", "TEA006", "TEA007"
    };

    /// <summary>
    /// Business partner names holding any of these fragments are excluded regardless of card code.
    /// Staff loan invoices are never delivered, so they can never carry a POD.
    /// </summary>
    public static readonly string[] ExcludedCardNameFragments = ["staff loan"];

    public static bool IsExcludedCardCode(string? cardCode) =>
        !string.IsNullOrWhiteSpace(cardCode) && ExcludedCardCodes.Contains(cardCode.Trim());

    public static bool IsExcludedCardName(string? cardName) =>
        !string.IsNullOrWhiteSpace(cardName) &&
        ExcludedCardNameFragments.Any(fragment =>
            cardName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static bool IsExcluded(string? cardCode, string? cardName) =>
        IsExcludedCardCode(cardCode) || IsExcludedCardName(cardName);

    /// <summary>
    /// Identifies an excluded business partner by whichever field triggered the exclusion, so the
    /// message names the reason rather than an unrelated card code.
    /// </summary>
    public static string DescribeExclusion(string? cardCode, string? cardName) =>
        IsExcludedCardName(cardName)
            ? cardName!.Trim()
            : cardCode?.Trim() ?? string.Empty;
}