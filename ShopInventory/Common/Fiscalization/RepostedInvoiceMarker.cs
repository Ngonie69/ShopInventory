using System.Text.RegularExpressions;
using ShopInventory.Configuration;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Recognises a SAP invoice reposted after the SAP Business One update, which must never be fiscalised.
/// </summary>
/// <remarks>
/// About 1,560 A/R invoices dated 20–22 Sep 2026 were fiscalised with ZIMRA when they were first posted,
/// then reposted into the live company by the separate back-posting tool, each under a new DocNum. The
/// tool starts every reposted invoice's remarks (<c>OINV.Comments</c>) with
/// <see cref="FiscalisationSettings.RepostedInvoiceCommentsPrefix"/> and names the old number after it:
/// <c>Invoice posted from SAP update. Old invoice 777418. …</c>.
///
/// Nothing else marks them. There is no sale row, no consolidation and no fiscal transaction under the
/// new DocNum, so neither registry recognises them and a read-back from the device finds nothing — the
/// invoice looks exactly like an ordinary unfiscalised one. The device's own duplicate guard is keyed on
/// the invoice number and cannot see that the two numbers are one sale, so fiscalising the repost files
/// the sale with ZIMRA a second time, and that cannot be withdrawn.
/// </remarks>
public static partial class RepostedInvoiceMarker
{
    /// <summary>
    /// Whether <paramref name="comments"/> start with the configured marker.
    /// </summary>
    /// <remarks>
    /// A blank prefix switches the guard off rather than matching every invoice.
    /// </remarks>
    public static bool IsReposted(FiscalisationSettings settings, string? comments)
    {
        var prefix = settings.RepostedInvoiceCommentsPrefix?.Trim();

        return !string.IsNullOrEmpty(prefix)
               && comments is not null
               && comments.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The number the invoice was first posted and fiscalised under, if the remarks name it.
    /// </summary>
    public static string? OldInvoiceNumber(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments))
        {
            return null;
        }

        var match = OldInvoicePattern().Match(comments);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Old invoice\s+#?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex OldInvoicePattern();
}
