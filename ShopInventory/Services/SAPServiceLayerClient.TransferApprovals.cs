using System.Net;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Inventory transfers keyed in the B1 client and held by SAP's approval procedure: the approved drafts
/// still waiting to be added, the approval requests behind them, and the add itself
/// (<c>StockTransferDrafts(n)/SaveDraftToDocument</c>).
/// </summary>
/// <remarks>
/// The list is read from the draft side rather than from <c>ApprovalRequests</c>. The draft is what
/// says a transfer is still waiting (<c>bost_Open</c>) and approved (<c>sasApproved</c>), and it
/// carries the warehouses, so a depot controller's queue is filtered — and counted — in SAP. On
/// KEFALOS_TEST_3 the two sides agree exactly: 144 open approved drafts, each with an approved request.
/// </remarks>
public partial class SAPServiceLayerClient
{
    private const string StockTransferDraftListSelect =
        "$select=DocEntry,DocNum,DocDate,DueDate,CreationDate,FromWarehouse,ToWarehouse,Comments,JournalMemo,DocumentStatus,AuthorizationStatus,DocObjectCode,AttachmentEntry";

    private const string ItemManagementSelect = "$select=ItemCode,ManageBatchNumbers,ManageSerialNumbers";

    private const string NewestStockTransferSelect = "$select=DocEntry";

    /// <summary>How many keys go into one <c>… eq … or …</c> filter, as for the credit memo drafts.</summary>
    private const int StockTransferKeyChunkSize = 20;

    /// <summary>
    /// How far past the newest transfer before an add the created one is looked for. Transfers are
    /// posted all day from tills, vans and the factory, so the one the add made need not be the next.
    /// </summary>
    private const int CreatedTransferSearchWindow = 50;

    #region Reads

    public async Task<(List<SAPStockTransferDraft> Items, int TotalCount)> GetApprovedStockTransferDraftsAsync(
        int page,
        int pageSize,
        int? beforeDocEntry = null,
        IReadOnlyCollection<string>? fromWarehouses = null,
        string? warehouse = null,
        CancellationToken cancellationToken = default)
    {
        var sources = fromWarehouses?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sources is { Count: 0 })
        {
            throw new ArgumentException(
                "A warehouse scope must name at least one warehouse; pass null to read every warehouse.", nameof(fromWarehouses));
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, DocumentListPageSize);
        var skip = (page - 1) * pageSize;

        // The set serves every kind of draft, so the object code is never optional.
        var baseFilter =
            $"DocObjectCode eq '{SapDocObjectCodes.StockTransfer}'"
            + $" and DocumentStatus eq '{SapDocumentStatuses.Open}'"
            + $" and AuthorizationStatus eq '{SapStockTransferAuthorizationStatuses.Approved}'";

        // In the base filter, so the count is of the queue this caller sees.
        if (sources is not null)
        {
            baseFilter += $" and ({string.Join(" or ", sources.Select(code => $"FromWarehouse eq '{EscapeODataStringLiteral(code)}'"))})";
        }

        if (!string.IsNullOrWhiteSpace(warehouse))
        {
            var literal = EscapeODataStringLiteral(warehouse.Trim());
            baseFilter += $" and (FromWarehouse eq '{literal}' or ToWarehouse eq '{literal}')";
        }

        var countUrl = $"StockTransferDrafts/$count?$filter={Uri.EscapeDataString(baseFilter)}";

        var listFilter = beforeDocEntry is int cursor ? $"{baseFilter} and DocEntry lt {cursor}" : baseFilter;
        var pagingClause = beforeDocEntry is null ? $"&$skip={skip}" : string.Empty;
        var listUrl =
            $"StockTransferDrafts?$filter={Uri.EscapeDataString(listFilter)}&{StockTransferDraftListSelect}"
            + $"&$orderby=DocEntry desc&$top={pageSize}{pagingClause}";

        await EnsureAuthenticatedAsync(cancellationToken);

        var pageTask = ReadSapJsonAsync<SAPResponse<SAPStockTransferDraft>>(
            listUrl, $"list approved stock transfer drafts (page {page})", cancellationToken, pageSize);
        var countTask = ReadSapCountAsync(countUrl, "count approved stock transfer drafts", cancellationToken);

        await Task.WhenAll(pageTask, countTask);

        return ((await pageTask)?.Value ?? [], await countTask);
    }

    public async Task<List<SAPApprovalRequest>> GetStockTransferApprovalRequestsAsync(
        IReadOnlyCollection<int> draftEntries,
        bool withLines = false,
        CancellationToken cancellationToken = default)
    {
        var keys = draftEntries.Where(key => key > 0).Distinct().OrderByDescending(key => key).ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        var select = withLines ? ApprovalRequestDetailSelect : ApprovalRequestSelect;
        var requests = new List<SAPApprovalRequest>(keys.Count);
        foreach (var chunk in keys.Chunk(StockTransferKeyChunkSize))
        {
            var filter =
                $"ObjectType eq '{SapObjectTypes.StockTransfer}' and ({string.Join(" or ", chunk.Select(key => $"DraftEntry eq {key}"))})";

            // A draft can be held by more than one template — three on KEFALOS_TEST_3 had two requests
            // each — so the page is sized for that rather than for one row per key.
            var pageSize = chunk.Length * 3;
            var page = await ReadSapJsonAsync<SAPResponse<SAPApprovalRequest>>(
                $"ApprovalRequests?$filter={Uri.EscapeDataString(filter)}&{select}&$orderby=Code desc&$top={pageSize}",
                $"read the approval requests of {chunk.Length} stock transfer draft(s)",
                cancellationToken,
                pageSize);
            requests.AddRange(page?.Value ?? []);
        }

        return requests;
    }

    public Task<SAPStockTransferDraft?> GetStockTransferDraftAsync(int docEntry, CancellationToken cancellationToken = default)
        // No $select, deliberately: naming StockTransferLines in one drops every line's BatchNumbers.
        => ReadSapJsonAsync<SAPStockTransferDraft>($"StockTransferDrafts({docEntry})", $"read stock transfer draft {docEntry}", cancellationToken);

    public async Task<IReadOnlyDictionary<string, SapItemManagement>> GetItemManagementAsync(
        IReadOnlyCollection<string> itemCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = itemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, SapItemManagement>(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0)
        {
            return result;
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        foreach (var chunk in codes.Chunk(StockTransferKeyChunkSize))
        {
            var filter = string.Join(" or ", chunk.Select(code => $"ItemCode eq '{EscapeODataStringLiteral(code)}'"));
            var page = await ReadSapJsonAsync<SAPResponse<Item>>(
                $"Items?$filter={Uri.EscapeDataString(filter)}&{ItemManagementSelect}&$top={chunk.Length}",
                $"read the batch and serial flags of {chunk.Length} item(s)",
                cancellationToken,
                chunk.Length);

            foreach (var item in page?.Value ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item.ItemCode))
                {
                    result[item.ItemCode] = new SapItemManagement(
                        item.ItemCode,
                        IsSapManaged(item.ManageBatchNumbers),
                        IsSapManaged(item.ManageSerialNumbers));
                }
            }
        }

        return result;
    }

    public async Task<int?> GetNewestStockTransferDocEntryAsync(CancellationToken cancellationToken = default)
    {
        var page = await ReadSapJsonAsync<SAPResponse<InventoryTransfer>>(
            $"StockTransfers?{NewestStockTransferSelect}&$orderby=DocEntry desc&$top=1",
            "read the newest stock transfer",
            cancellationToken,
            pageSize: 1);

        return page?.Value?.FirstOrDefault()?.DocEntry;
    }

    public async Task<List<InventoryTransfer>> GetStockTransfersAfterAsync(
        int afterDocEntry,
        string? fromWarehouse,
        string? toWarehouse,
        CancellationToken cancellationToken = default)
    {
        var filter = $"DocEntry gt {afterDocEntry}";
        if (!string.IsNullOrWhiteSpace(fromWarehouse))
        {
            filter += $" and FromWarehouse eq '{EscapeODataStringLiteral(fromWarehouse.Trim())}'";
        }

        if (!string.IsNullOrWhiteSpace(toWarehouse))
        {
            filter += $" and ToWarehouse eq '{EscapeODataStringLiteral(toWarehouse.Trim())}'";
        }

        // Lines come back without their batches here (a several-document read), which is fine: the
        // created transfer is recognised by its items and quantities.
        var page = await ReadSapJsonAsync<SAPResponse<InventoryTransfer>>(
            $"StockTransfers?$filter={Uri.EscapeDataString(filter)}&{StockTransferSelect}&$orderby=DocEntry asc&$top={CreatedTransferSearchWindow}",
            $"read the stock transfers posted after {afterDocEntry}",
            cancellationToken,
            CreatedTransferSearchWindow);

        return page?.Value ?? [];
    }

    #endregion

    #region Writes

    public async Task<int?> SaveStockTransferDraftToDocumentAsync(int draftDocEntry, CancellationToken cancellationToken = default)
    {
        var operation = $"save stock transfer draft {draftDocEntry} to document";
        _logger.LogInformation("Saving SAP stock transfer draft {DraftEntry} to its document", draftDocEntry);

        // The bound action on the draft itself; the generic DraftsService_SaveDraftToDocument takes a
        // Document, which an inventory transfer is not.
        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Post, $"StockTransferDrafts({draftDocEntry})/SaveDraftToDocument"),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ReadDocEntry(body);
    }

    #endregion
}
