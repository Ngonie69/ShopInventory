using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

/// <summary>Names item codes the orders did not. Best-effort: a code it cannot name is left out.</summary>
internal delegate Task<IReadOnlyDictionary<string, string?>> ItemNameSource(
    IReadOnlyCollection<string> itemCodes,
    CancellationToken cancellationToken);

/// <summary>
/// Gives van order lines their item names. The van app sends order lines with an item code and no
/// description, so without this every product on the order reports is a bare code.
/// </summary>
/// <remarks>
/// A name comes from another order line for the same item, then from a van sale line for it, then from
/// <see cref="ItemNameSource"/> (SAP). The API's own <c>Products</c> table is never written, so it is
/// no help here.
/// </remarks>
internal static class OrderItemNames
{
    /// <summary>SAP's item names, or none when SAP is off or not wired in.</summary>
    public static ItemNameSource? FromSap(ISAPServiceLayerClient? sapClient, SAPSettings? settings, ILogger? logger)
    {
        if (sapClient is null || settings is not { Enabled: true })
        {
            return null;
        }

        return async (codes, cancellationToken) =>
        {
            try
            {
                return await sapClient.GetItemNamesAsync(codes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A report is still worth reading with codes in place of names.
                logger?.LogWarning(ex, "Could not read {Count} item names for the order reports", codes.Count);
                return new Dictionary<string, string?>();
            }
        };
    }

    /// <summary>
    /// A name for every item code that has one anywhere: on its own order lines, on a van sale line, or
    /// from <paramref name="source"/>. Codes are trimmed and upper-cased.
    /// </summary>
    public static async Task<Dictionary<string, string>> ResolveAsync(
        IEnumerable<(string ItemCode, string? Description)> orderLines,
        IEnumerable<VanSaleLineFact> saleLines,
        ItemNameSource? source,
        CancellationToken cancellationToken)
    {
        var lines = orderLines.ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, description) in lines)
        {
            AddName(names, code, description);
        }

        var missing = Missing(lines, names);
        if (missing.Count == 0)
        {
            return names;
        }

        foreach (var line in saleLines)
        {
            if (missing.Contains(Normalise(line.ItemCode)))
            {
                AddName(names, line.ItemCode, line.ItemDescription);
            }
        }

        missing = Missing(lines, names);
        if (missing.Count == 0 || source is null)
        {
            return names;
        }

        foreach (var (code, name) in await source(missing, cancellationToken))
        {
            AddName(names, code, name);
        }

        return names;
    }

    private static HashSet<string> Missing(List<(string ItemCode, string? Description)> lines, Dictionary<string, string> names) =>
        lines
            .Select(line => Normalise(line.ItemCode))
            .Where(code => code.Length > 0 && !names.ContainsKey(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AddName(Dictionary<string, string> names, string? code, string? name)
    {
        var key = Normalise(code);
        if (key.Length > 0 && !string.IsNullOrWhiteSpace(name))
        {
            names.TryAdd(key, name.Trim());
        }
    }

    private static string Normalise(string? code) => code?.Trim().ToUpperInvariant() ?? string.Empty;
}
