using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The VAT group each item is sold under, from the local copy of the item master.
/// </summary>
/// <remarks>
/// Shared by every route that fiscalises a sale before SAP has seen it — the till, and the van sale
/// fiscalised ahead of its invoice. SAP would otherwise be the one to pick each line's VAT group, and it
/// picks after the receipt has already been signed; a receipt that declared one rate and an invoice that
/// charged another cannot both be right, and only one of them can be corrected. So the code is decided
/// here, once, and handed to both.
///
/// <para>
/// The local copy and never SAP directly. Reading the item master costs a paged sweep of every valid item
/// against a concurrency limit shared with everything else the process does, and a customer at a counter
/// is the worst person to charge for it — the Item Tax Groups sync in Settings → Data Sync pays it instead.
/// </para>
///
/// <para>
/// Empty on any failure, and empty is safe: a line with no answer keeps whatever the request said, which
/// is what every line had before this existed. A sale is never refused over a tax lookup — the customer is
/// at the counter and the basket is already rung up.
/// </para>
/// </remarks>
public static class ItemVatGroups
{
    public static async Task<Dictionary<string, string>> ResolveAsync(
        ApplicationDbContext context,
        IEnumerable<string?> itemCodes,
        ILogger logger,
        CancellationToken ct)
    {
        var codes = itemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var rows = await context.SapItemTaxGroups
                .AsNoTracking()
                .Where(row => codes.Contains(row.ItemCode))
                .ToListAsync(ct);

            var resolved = rows
                .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().VatGroup, StringComparer.OrdinalIgnoreCase);

            // Named, not counted. An item with no VAT group is sold at the standard rate, so this is
            // the line that says which customer was overcharged and on what - and the first sign
            // that the nightly warm has not run or that an item is newer than its last pass.
            var missing = codes.Where(code => !resolved.ContainsKey(code)).ToList();
            if (missing.Count > 0)
            {
                logger.LogWarning(
                    "No VAT group stored for {Count} item(s) on this sale: {Items}. They are taxed at "
                    + "the standard rate, which is wrong for anything zero-rated or exempt.",
                    missing.Count, string.Join(", ", missing));
            }

            return resolved;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not read item VAT groups; this sale is taxed at the standard rate throughout.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The tax code one line is charged and declared under: the item master first, and what the caller
    /// already had only where the master has no answer.
    /// </summary>
    public static string? TaxCodeFor(
        string? itemCode,
        string? requestedTaxCode,
        IReadOnlyDictionary<string, string> vatGroups)
    {
        var code = itemCode?.Trim();

        return !string.IsNullOrEmpty(code) && vatGroups.TryGetValue(code, out var vatGroup)
            ? vatGroup
            : requestedTaxCode;
    }
}
