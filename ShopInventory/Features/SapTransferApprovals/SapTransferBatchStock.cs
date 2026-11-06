using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapTransferApprovals;

/// <summary>
/// What each batch a held draft picks still holds in the warehouse it is picked from.
/// </summary>
/// <remarks>
/// <para>A draft records its batch picks when it is keyed, and an approved draft can wait weeks. By then
/// the factory has issued those batches first in, first out, and SAP refuses the add with the same
/// words it uses for a line with no pick at all: <c>-4014 Cannot add row without complete selection of
/// batch/serial numbers</c>. That is what the first add on KEFALOS_TEST_3 answered (2026-11-06, draft
/// 79744): its one line picked <c>YOG058#M11/23/N</c>, which CMFC no longer held, while two later
/// batches did. Saying which batch ran out is the only thing that tells somebody what to fix.</para>
///
/// <para>Read through the fixed, parameterised <c>ITEM_BATCHES_V1</c> query, one read per item and
/// warehouse, because it mints nothing: the other batch reads build a SQLQueries object per warehouse
/// or item family, and SAP cannot delete those.</para>
/// </remarks>
internal static class SapTransferBatchStock
{
    /// <summary>
    /// How many item reads run at once — half of <c>SAP:MaxConcurrentRequests</c>, which is the whole
    /// application's budget, as for the approval lookups.
    /// </summary>
    private const int ReadConcurrency = 3;

    /// <summary>Batch number → quantity on hand, per item and warehouse (keyed by <see cref="Key"/>).</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>> ReadAsync(
        ISAPServiceLayerClient sap,
        SAPStockTransferDraft draft,
        CancellationToken cancellationToken)
    {
        var pairs = (draft.StockTransferLines ?? [])
            .Where(line => line.BatchNumbers is { Count: > 0 } && !string.IsNullOrWhiteSpace(line.ItemCode))
            .Select(line => (Item: line.ItemCode!.Trim(), Warehouse: (line.FromWarehouseCode ?? draft.FromWarehouse ?? string.Empty).Trim()))
            .Where(pair => pair.Warehouse.Length > 0)
            .DistinctBy(pair => Key(pair.Item, pair.Warehouse))
            .ToList();

        var stock = new Dictionary<string, IReadOnlyDictionary<string, decimal>>(StringComparer.Ordinal);
        if (pairs.Count == 0)
        {
            return stock;
        }

        using var slots = new SemaphoreSlim(ReadConcurrency, ReadConcurrency);
        var gate = new Lock();
        await Task.WhenAll(pairs.Select(async pair =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var batches = await sap.GetBatchNumbersForItemInWarehouseAsync(pair.Item, pair.Warehouse, cancellationToken);
                var onHand = batches
                    .Where(batch => !string.IsNullOrWhiteSpace(batch.BatchNum))
                    .GroupBy(batch => batch.BatchNum!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(batch => batch.Quantity), StringComparer.OrdinalIgnoreCase);
                lock (gate)
                {
                    stock[Key(pair.Item, pair.Warehouse)] = onHand;
                }
            }
            finally
            {
                slots.Release();
            }
        }));

        return stock;
    }

    public static string Key(string item, string warehouse) =>
        $"{item.Trim().ToUpperInvariant()}|{warehouse.Trim().ToUpperInvariant()}";
}
