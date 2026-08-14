namespace ShopInventory.Common.Sales;

/// <summary>
/// The references the system reserves for its own use in SAP's <c>U_Van_saleorder</c> UDF.
/// </summary>
/// <remarks>
/// That one field is how several routes ask SAP "do you already hold this document?" before posting
/// it, and it is also the local idempotency key on invoice creation. Van sales, shop till sales,
/// vending, end-of-day consolidation and stock reservations all write to it.
///
/// It is also settable by any caller of <c>POST /api/Invoice</c>, and that is the hazard. The failure
/// is not a duplicate but its quieter opposite: a value colliding with a till sale's reference makes
/// the posting service's pre-post probe find that unrelated invoice, adopt it, and mark the sale
/// posted against a document that has nothing to do with it. The sale is then never really invoiced
/// and nothing looks wrong.
///
/// So a client may not write into the part of the namespace the system generates for itself.
/// </remarks>
public static class SaleReferenceNamespace
{
    /// <summary>A desktop sale created without the caller supplying its own reference.</summary>
    public const string DesktopSalePrefix = "DS-";

    /// <summary>An end-of-day consolidated invoice: CONSOL-{yyyyMMdd}-{cardCode}.</summary>
    public const string ConsolidationPrefix = "CONSOL-";

    public static readonly string[] ReservedPrefixes = [DesktopSalePrefix, ConsolidationPrefix];

    /// <summary>
    /// Whether the reference belongs to the system rather than to the caller.
    /// </summary>
    /// <remarks>
    /// Prefixes cover what the server generates. What a till generates has no fixed shape this side
    /// knows — it is whatever the client chose — so those are caught by looking the reference up
    /// among the sales instead, which needs no agreement about formats between the two codebases.
    /// </remarks>
    public static bool IsReserved(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var trimmed = reference.Trim();

        return ReservedPrefixes.Any(
            prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
