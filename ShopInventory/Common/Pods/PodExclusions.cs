namespace ShopInventory.Common.Pods;

public static class PodExclusions
{
    public static readonly HashSet<string> ExcludedCardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CIS006", "MAC009", "MAC006", "COR007", "COR006", "COR008",
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013",
        "VAN014", "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020",
        "STA040", "STA041", "STA042", "STA043", "STA044", "STA045", "STA046", "STA047", "STA048",
        "PRO030", "PRO031", "PRO032", "PRO033", "PRO034", "PRO035", "PRO036",
        "CAS004(FCA)", "DON004", "TEA006", "TEA007"
    };

    public static bool IsExcludedCardCode(string? cardCode) =>
        !string.IsNullOrWhiteSpace(cardCode) && ExcludedCardCodes.Contains(cardCode.Trim());
}