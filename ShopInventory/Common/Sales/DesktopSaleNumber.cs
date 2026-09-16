namespace ShopInventory.Common.Sales;

/// <summary>
/// The short, app-issued number a sale is known by to the people who handle it — <c>INV10427</c>.
/// </summary>
/// <remarks>
/// A sale's identifier in this app until now was the reference the till or handset issued it under —
/// <c>KEF-FAC-20260915-1F3EBB3AD785</c>. That is the right key for fiscal and support work, because it
/// is what the device signed and what its own logs hold, and it is the wrong thing to ask a person to
/// read: twelve hex characters carry no shape, so two references from the same day are told apart only
/// by spelling them out. Picking the sale to credit off a list of them is the job this exists for.
///
/// <para>
/// <b>This is not a new number.</b> It is the format the KefShop till has always printed on the
/// customer's receipt: <c>ReceiptRenderer.Transaction</c> writes <c>INV{InvoiceId}</c>, and
/// <c>InvoiceId</c> is the <c>saleId</c> this API returned when the sale was created. The number was
/// already in the customer's hand and already on the reprint; the console was the only place that had
/// never shown it, which is why a return could not be matched to its sale. Nothing is generated, nothing
/// is stored and no till change is needed — the two ends were already using the same number and only
/// one end was displaying it. Changing this format silently un-matches every receipt ever printed, so
/// it must not be changed without changing the till in the same breath.
/// </para>
///
/// <para>
/// The number is the sale row's primary key. It is already unique, already monotonic, already assigned
/// before the response leaves the handler, and already carried on every sale the app has ever taken, so
/// every historical row is readable the moment it is displayed. A separate sequence column would have
/// bought a per-day reset and cost a migration, a backfill for rows that would have none, a second thing
/// that can disagree with the first about which sale is which — and a receipt that no longer matched.
/// </para>
///
/// <para>
/// It counts across every channel together — till, van and vending share the table — so one till's
/// sales are not consecutive. That is the price of not storing anything; the number names a sale, it
/// does not count them. Reports that count sales count rows.
/// </para>
/// </remarks>
public static class DesktopSaleNumber
{
    /// <summary>
    /// The prefix the number carries, matching what the till prints. See the type remarks before
    /// changing it: it is half of a contract with a piece of paper in a customer's hand.
    /// </summary>
    public const string Prefix = "INV";

    /// <summary>The sale number for a sale row, as it is displayed, printed and searched for.</summary>
    public static string Format(int saleId) => $"{Prefix}{saleId}";

    /// <summary>
    /// Reads a sale number the way a person types it: with the prefix or without, in any case, and
    /// tolerating the spacing, hyphen and hash a person adds — <c>INV10427</c>, <c>inv 10427</c>,
    /// <c>INV-10427</c>, <c>#10427</c> and <c>10427</c> are all the same number.
    /// </summary>
    /// <returns><c>true</c> and the sale id when the text is a sale number; <c>false</c> otherwise.</returns>
    public static bool TryParse(string? text, out int saleId)
    {
        saleId = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('#').Trim();

        // The prefix is optional, and so is the separator a person puts after it.
        if (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[Prefix.Length..].TrimStart('-', ' ');

        // Positive only. Zero and negatives name no row, and letting them parse would turn a stray
        // "-" into a search that quietly matched nothing.
        return int.TryParse(trimmed, out var parsed) && parsed > 0 && (saleId = parsed) > 0;
    }

    /// <summary>
    /// Whether the text names a sale number <em>and says so</em>, by carrying the prefix.
    /// </summary>
    /// <remarks>
    /// This is what lets a search be exact. A bare <c>10427</c> could be a sale number or a SAP
    /// document number and is tried as both; <c>INV10427</c> can only be the first, so a search that
    /// spells it out is not widened with unrelated rows that happen to share the digits.
    ///
    /// <para>
    /// It requires the number to parse as well as the prefix to match, so that a customer or a
    /// reference beginning with these three letters — "INVERTER", say — is still found by the ordinary
    /// text search rather than being read as a malformed sale number and matching nothing.
    /// </para>
    /// </remarks>
    public static bool NamesSaleNumber(string? text) =>
        TryParse(text, out _)
        && text!.TrimStart().TrimStart('#').TrimStart()
            .StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
