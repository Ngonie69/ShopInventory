namespace ShopInventory.Web.Common;

/// <summary>
/// The van sales business partners. Stock booked to these accounts moves onto a
/// sales van rather than to a customer anybody reps, so nothing they buy belongs
/// in a sales rep's figures — they are large enough to bury every real customer
/// in a ranking.
///
/// The same codes are excluded from POD follow-up (see PodExclusions in the API
/// and the list ProofOfDelivery.razor carries), where they are one entry in a
/// wider set of never-delivered accounts.
/// </summary>
public static class VanSalesAccounts
{
    private static readonly HashSet<string> CardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013", "VAN014",
        "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020"
    };

    public static bool IsVanSalesAccount(string? cardCode) =>
        !string.IsNullOrWhiteSpace(cardCode) && CardCodes.Contains(cardCode.Trim());
}
