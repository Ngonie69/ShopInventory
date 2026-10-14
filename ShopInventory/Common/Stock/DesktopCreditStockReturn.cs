namespace ShopInventory.Common.Stock;

/// <summary>
/// The key a desktop credit's returned units are journalled under in <c>StockMovements</c>.
/// </summary>
/// <remarks>
/// Named once because it is written in one place and read in another: the credit poster returns the
/// units under it, and the till's stock pages find the returns by it. A return is journalled as a
/// <c>Release</c>, the same kind a refused sale's claim is handed back under, so the key is the only
/// thing that says a movement was a credit note.
/// </remarks>
public static class DesktopCreditStockReturn
{
    public const string KeyPrefix = "desktop-credit:";

    /// <param name="creditNumber">The credit's stored number — <c>DCN-…</c>, as filed with ZIMRA.</param>
    public static string DocumentKey(string creditNumber) => KeyPrefix + creditNumber;

    /// <summary>The credit's stored number, read back out of a movement's key; null for any other movement.</summary>
    public static string? CreditNumberOf(string? documentKey) =>
        documentKey is not null && documentKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            ? documentKey[KeyPrefix.Length..]
            : null;
}
