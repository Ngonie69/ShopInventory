namespace ShopInventory.Web.Common;

/// <summary>
/// The van sales business partners. Stock booked to these accounts moves onto a
/// sales van rather than to a customer anybody reps, so nothing they buy belongs
/// in a sales rep's figures — they are large enough to bury every real customer
/// in a ranking.
///
/// The API holds the same list in ShopInventory.Common.Sales.VanSalesAccounts,
/// where it drops these accounts from the Top Customers report and from POD
/// follow-up. The two projects do not reference each other, so the copies are
/// held equal by a test rather than by the compiler.
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
