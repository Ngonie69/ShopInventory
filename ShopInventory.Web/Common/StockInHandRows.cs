using ShopInventory.Web.Models;

namespace ShopInventory.Web.Common;

/// <summary>One item on /stock-in-hand.</summary>
/// <param name="Code">The item's SAP code.</param>
/// <param name="Name">The item's description.</param>
/// <param name="UoM">The inventory unit of measure; null when SAP gives none.</param>
/// <param name="Then">On hand at the moment picked; equal to <see cref="Now"/> for Now.</param>
/// <param name="Now">On hand now.</param>
/// <param name="Committed">SAP's committed figure; known for Now only, zero otherwise.</param>
public sealed record StockInHandRow(
    string Code,
    string Name,
    string? UoM,
    decimal Then,
    decimal Now,
    decimal Committed)
{
    public decimal Free => Now - Committed;
    public decimal Change => Now - Then;
}

/// <summary>One line of an item's movements, opening rows included.</summary>
public sealed record StockInHandEntry(
    DateTime AtUtc,
    string Label,
    string? Reference,
    decimal? Quantity,
    decimal Balance,
    bool IsOpening);

/// <summary>
/// The arithmetic behind /stock-in-hand, kept out of the page so it can be tested without one.
/// </summary>
public static class StockInHandRows
{
    /// <summary>Now: SAP's figures, as the stock cache holds them.</summary>
    public static List<StockInHandRow> FromLive(IEnumerable<ProductDto> products) =>
        products
            .Where(p => !string.IsNullOrWhiteSpace(p.ItemCode))
            .Select(p => new StockInHandRow(
                p.ItemCode!.Trim(),
                NameOr(p.ItemName, p.ItemCode!),
                p.UoM,
                p.QuantityInStock,
                p.QuantityInStock,
                p.QuantityCommitted))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A past moment against now, both rebuilt by the ledger, so the two columns come from one source
    /// and their difference is what the ledger moved. When the ledger cannot describe now (today's
    /// snapshot failed), SAP's live figure stands in for it.
    /// </summary>
    public static List<StockInHandRow> FromLedger(
        StockAsAtResponse then,
        StockAsAtResponse? now,
        IEnumerable<ProductDto> live)
    {
        var liveByCode = live
            .Where(p => !string.IsNullOrWhiteSpace(p.ItemCode))
            .GroupBy(p => p.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var thenByCode = then.Items.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
        var nowByCode = now is { Recorded: true }
            ? now.Items.ToDictionary(i => i.ItemCode, i => i.OnHand, StringComparer.OrdinalIgnoreCase)
            : liveByCode.ToDictionary(p => p.Key, p => p.Value.QuantityInStock, StringComparer.OrdinalIgnoreCase);

        return thenByCode.Keys
            .Union(nowByCode.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(code =>
            {
                thenByCode.TryGetValue(code, out var past);
                liveByCode.TryGetValue(code, out var product);
                return new StockInHandRow(
                    code,
                    NameOr(product?.ItemName ?? past?.ItemName, code),
                    product?.UoM,
                    past?.OnHand ?? 0m,
                    nowByCode.GetValueOrDefault(code),
                    0m);
            })
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// An item's movements oldest first, with each morning's count from SAP as a row of its own: a
    /// ledger day starts from SAP's figure, not from the previous day's close, so the running
    /// balance has to restart there rather than appear to jump.
    /// </summary>
    public static List<StockInHandEntry> Ledger(StockMovementsResponse response)
    {
        var entries = response.Movements
            .Select(m => new StockInHandEntry(
                m.OccurredAtUtc,
                StockLedgerKinds.Describe(m.Kind, m.Quantity),
                m.Reference,
                m.Quantity,
                m.BalanceAfter,
                false))
            .Concat(response.Openings.Select(o => new StockInHandEntry(
                o.TakenAtUtc,
                "Morning count from SAP",
                null,
                null,
                o.Quantity,
                true)))
            .OrderBy(e => e.AtUtc)
            .ThenBy(e => e.IsOpening ? 0 : 1)
            .ToList();

        return entries;
    }

    /// <summary>
    /// What the item held at the start of a window that begins mid-day: the first movement's balance
    /// before it was applied, or <paramref name="fallback"/> when nothing moved.
    /// </summary>
    public static decimal StartBalance(StockMovementsResponse response, decimal fallback)
    {
        var first = response.Movements.FirstOrDefault();
        if (first is null) return response.Openings.FirstOrDefault()?.Quantity ?? fallback;

        var firstOpening = response.Openings.FirstOrDefault();
        if (firstOpening is not null && firstOpening.TakenAtUtc <= first.OccurredAtUtc)
            return fallback;

        return first.BalanceAfter - first.Quantity;
    }

    private static string NameOr(string? name, string code) =>
        string.IsNullOrWhiteSpace(name) ? code.Trim() : name.Trim();
}
