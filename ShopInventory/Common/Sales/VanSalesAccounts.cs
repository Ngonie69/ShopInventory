namespace ShopInventory.Common.Sales;

/// <summary>
/// The van sales business partners. Stock booked to these accounts moves onto a
/// sales van rather than out to a customer anybody sells to, so they are not a
/// customer in the sense any ranking means — and they trade at a scale that
/// buries every real account below them.
///
/// The Web mirrors this list in ShopInventory.Web.Common.VanSalesAccounts for
/// the sales rep dashboard, which ranks from its own order window rather than
/// from this report. A test holds the two copies equal.
/// </summary>
public static class VanSalesAccounts
{
    /// <summary>VAN008 through VAN020. The lower van codes are warehouses, not business partners.</summary>
    public static readonly IReadOnlySet<string> CardCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013", "VAN014",
        "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020"
    };

    public static bool IsVanSalesAccount(string? cardCode) =>
        !string.IsNullOrWhiteSpace(cardCode) && CardCodes.Contains(cardCode.Trim());
}
