using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The short number a desktop credit is known by to people — <c>CN1753</c> — in the shape of the sale
/// it credits, <c>INV1753</c>.
/// </summary>
/// <remarks>
/// <para>
/// A credit's stored <c>Number</c> is <c>DCN-</c> and 32 hex characters. That is the invoice number the
/// credit was filed with at ZIMRA, so it cannot change, and it is no more readable than the till
/// reference <see cref="DesktopSaleNumber"/> exists to replace. This is the number a person reads.
/// </para>
/// <para>
/// Nothing is stored. It is the sale's id, so a credit names its sale at a glance, and a second or later
/// credit on the same sale takes a suffix in the order the credits were raised: <c>CN1753</c>,
/// <c>CN1753-2</c>. Credits are never deleted — a refused one keeps its row and its number — so the
/// numbering of a sale's credits never shifts.
/// </para>
/// </remarks>
public static class DesktopCreditNoteNumber
{
    public const string Prefix = "CN";

    /// <param name="ordinal">1 for the sale's first credit, 2 for the second, and so on.</param>
    public static string Format(int saleId, int ordinal) =>
        ordinal <= 1 ? $"{Prefix}{saleId}" : $"{Prefix}{saleId}-{ordinal}";

    /// <summary>
    /// Reads a credit number the way a person types it — <c>CN1753</c>, <c>cn 1753</c>, <c>CN1753-2</c> —
    /// and answers the sale it names. The prefix is required: a bare number is a sale or SAP number.
    /// </summary>
    public static bool TryParseSale(string? text, out int saleId)
    {
        saleId = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('#').Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        trimmed = trimmed[Prefix.Length..].TrimStart('-', ' ');
        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
            trimmed = trimmed[..dash];

        return int.TryParse(trimmed, out var parsed) && parsed > 0 && (saleId = parsed) > 0;
    }

    /// <summary>The number of every credit on the given sales, by credit id.</summary>
    public static async Task<Dictionary<Guid, string>> ForSalesAsync(
        ApplicationDbContext db, IEnumerable<int> saleIds, CancellationToken ct)
    {
        var ids = saleIds.Distinct().ToList();

        var notes = await db.DesktopCreditNotes.AsNoTracking()
            .Where(n => ids.Contains(n.SaleId))
            .Select(n => new { n.Id, n.SaleId, n.CreatedAtUtc })
            .ToListAsync(ct);

        return notes
            .GroupBy(n => n.SaleId)
            .SelectMany(sale => sale
                .OrderBy(n => n.CreatedAtUtc)
                .ThenBy(n => n.Id)
                .Select((n, index) => (n.Id, Number: Format(sale.Key, index + 1))))
            .ToDictionary(t => t.Id, t => t.Number);
    }
}
