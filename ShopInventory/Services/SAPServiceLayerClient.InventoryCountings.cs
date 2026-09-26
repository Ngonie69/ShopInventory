using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The Inventory Counting surface: SAP's stock counts, read so their variance can be valued.
/// </summary>
/// <remarks>
/// <c>InventoryCountings</c> is its own entity type, not a <c>Document</c>: the key is
/// <c>DocumentEntry</c> (not <c>DocEntry</c>), the number is <c>DocumentNumber</c>, and the lines are
/// <c>InventoryCountingLines</c>. Nothing here writes — closing a count is B1's Inventory Posting.
/// </remarks>
public partial class SAPServiceLayerClient
{
    /// <summary>
    /// A count's header, without its lines: the list has to stay cheap however long the counts are.
    /// </summary>
    private const string InventoryCountingHeaderSelect =
        "$select=DocumentEntry,DocumentNumber,CountDate,CountTime,SingleCounterType,SingleCounterID," +
        "DocumentStatus,Remarks,Reference2,CountingType";

    private const string InventoryCountingDetailSelect = InventoryCountingHeaderSelect + ",InventoryCountingLines";

    /// <inheritdoc cref="ISAPServiceLayerClient.GetInventoryCountingsAsync" />
    public async Task<List<InventoryCounting>> GetInventoryCountingsAsync(
        string? documentStatus,
        string? search,
        int top,
        CancellationToken cancellationToken = default)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(documentStatus))
        {
            clauses.Add($"DocumentStatus eq '{EscapeODataStringLiteral(documentStatus.Trim())}'");
        }

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // A count is found by the number B1 shows on it, or by what somebody wrote in its
            // remarks — "31 December 2023-Diesel" is how these counts are told apart in practice.
            var remarks = $"contains(Remarks,'{EscapeODataStringLiteral(term)}')";
            clauses.Add(int.TryParse(term, out var number)
                ? $"(DocumentNumber eq {number} or {remarks})"
                : remarks);
        }

        var filter = clauses.Count == 0
            ? string.Empty
            : $"$filter={Uri.EscapeDataString(string.Join(" and ", clauses))}&";

        var url = $"InventoryCountings?{filter}{InventoryCountingHeaderSelect}&$orderby=DocumentEntry desc&$top={top}";

        var page = await ReadSapJsonAsync<SAPResponse<InventoryCounting>>(
            url, "read the inventory counts", cancellationToken, pageSize: top);

        return page?.Value ?? [];
    }

    /// <inheritdoc cref="ISAPServiceLayerClient.GetInventoryCountingAsync" />
    public Task<InventoryCounting?> GetInventoryCountingAsync(
        int documentEntry,
        CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<InventoryCounting>(
            $"InventoryCountings({documentEntry})?{InventoryCountingDetailSelect}",
            $"read inventory count {documentEntry}",
            cancellationToken);
}
