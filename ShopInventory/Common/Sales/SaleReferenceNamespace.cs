using System.Security.Cryptography;
using System.Text;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The references the system reserves for its own use in SAP's <c>U_Van_saleorder</c> UDF.
/// </summary>
/// <remarks>
/// That one field is how several routes ask SAP "do you already hold this document?" before posting
/// it, and it is also the local idempotency key on invoice creation. Van sales, shop till sales,
/// vending, end-of-day consolidation, stock reservations and web invoices all write to it.
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

    /// <summary>
    /// An invoice posted through <c>POST /api/Invoice</c> by a caller that named no business key of
    /// its own, derived from that caller's idempotency key.
    /// </summary>
    public const string WebInvoicePrefix = "WEB-";

    public static readonly string[] ReservedPrefixes = [DesktopSalePrefix, ConsolidationPrefix, WebInvoicePrefix];

    /// <summary>
    /// The longest reference this class will build, chosen to sit well inside any plausible size for
    /// the UDF. The existing producers are shorter still — <c>CONSOL-20260810-ABS006</c> is 22.
    /// </summary>
    private const int MaxDerivedKeyLength = 40;

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

    /// <summary>
    /// The SAP business key for an invoice whose caller supplied only an idempotency key.
    /// </summary>
    /// <remarks>
    /// <para>Without this a web invoice reaches SAP carrying no reference at all, and
    /// <c>ClientRequestId</c> is never sent to the Service Layer — so there is nothing to ask SAP
    /// about afterwards. A post whose reply is lost then leaves an invoice that no retry can find,
    /// and the retry posts a second one. Every other producer in this namespace has had a key to
    /// probe on for exactly this reason; this gives the interactive route one too.</para>
    ///
    /// <para>The result is deterministic in the caller's key, because that is the whole point: the
    /// same retry must derive the same reference or the probe looks for the wrong thing.</para>
    ///
    /// <para>An idempotency key is an arbitrary header value, and this one ends up inside an OData
    /// string literal and in a SAP UDF of unpublished width. A key that is already short and plainly
    /// safe is kept verbatim, so support can search SAP for the value the client actually sent;
    /// anything else is hashed down to something that always is. Both shapes carry the prefix, so
    /// neither can collide with a till or consolidation reference — and <see cref="IsReserved"/>
    /// refuses a caller who tries to hand-write one.</para>
    /// </remarks>
    public static string ForClientRequest(string clientRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRequestId);

        var trimmed = clientRequestId.Trim();

        return WebInvoicePrefix + (IsSafeVerbatim(trimmed) ? trimmed : Fingerprint(trimmed));
    }

    /// <summary>
    /// Whether the key can go into an OData literal and a UDF unaltered. Deliberately narrow: the
    /// quote, semicolon, <c>--</c> and <c>/*</c> that <c>SanitizeODataValue</c> rejects outright are
    /// only the ones we know about, and a key is not worth a round of escaping rules.
    /// </summary>
    private static bool IsSafeVerbatim(string key) =>
        key.Length <= MaxDerivedKeyLength - WebInvoicePrefix.Length
        && !key.Contains("--", StringComparison.Ordinal)
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
}
