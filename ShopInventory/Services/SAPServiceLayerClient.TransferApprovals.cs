using System.Net;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Inventory transfers and transfer requests keyed in the B1 client and held by SAP's approval procedure:
/// the approved drafts still waiting to be added, the approval requests behind them, and the add itself
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

    /// <summary>A draft this app saved for SAP's approval, found again by its request marker.</summary>
    private const string StockTransferDraftMarkerSelect =
        "$select=DocEntry,DocNum,DocDate,CreationDate,FromWarehouse,ToWarehouse,DocumentStatus,AuthorizationStatus,DocObjectCode,U_OrderNumber";

    private const string ApprovalTemplateConditionSelect =
        "$select=Code,Name,IsActive,UseTerms,ApprovalTemplateDocuments,ApprovalTemplateTerms,ApprovalTemplateQueries";

    private const string UserQuerySelect = "$select=InternalKey,QueryCategory,QueryDescription,Query";

    /// <summary>How many query keys go into one <c>InternalKey eq … or …</c> filter.</summary>
    private const int UserQueryKeyChunkSize = 20;

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
        SapTransferDraftFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, DocumentListPageSize);
        var skip = (page - 1) * pageSize;

        var baseFilter = ApprovedDraftQueueFilter(ScopeOf(fromWarehouses), filter);

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

    public async Task<int> CountApprovedStockTransferDraftsAsync(
        IReadOnlyCollection<string>? fromWarehouses = null,
        SapTransferDraftFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var baseFilter = ApprovedDraftQueueFilter(ScopeOf(fromWarehouses), filter);

        await EnsureAuthenticatedAsync(cancellationToken);

        return await ReadSapCountAsync(
            $"StockTransferDrafts/$count?$filter={Uri.EscapeDataString(baseFilter)}",
            $"count approved {(filter?.Kind == SapHeldDraftKind.Request ? "transfer request" : "stock transfer")} drafts",
            cancellationToken);
    }

    /// <summary>
    /// The approved, still-open drafts of the kinds asked for, in the caller's warehouses, narrowed by
    /// the page's filters — the whole <c>$filter</c>, so the list and its counts agree. Internal so the
    /// exact OData can be pinned by a unit test.
    /// </summary>
    internal static string ApprovedDraftQueueFilter(IReadOnlyList<string>? sources, SapTransferDraftFilter? filter)
    {
        // The set serves every kind of draft, so the object code is never optional.
        var baseFilter =
            DraftKindClause(filter?.Kind)
            + $" and DocumentStatus eq '{SapDocumentStatuses.Open}'"
            + $" and AuthorizationStatus eq '{SapStockTransferAuthorizationStatuses.Approved}'";

        // In the base filter, so the count is of the queue this caller sees.
        if (sources is not null)
        {
            baseFilter += $" and ({string.Join(" or ", sources.Select(code => $"FromWarehouse eq '{EscapeODataStringLiteral(code)}'"))})";
        }

        return baseFilter + DraftQueueFilterClauses(filter);
    }

    /// <summary>Which object codes the read admits: transfers, transfer requests, or both.</summary>
    internal static string DraftKindClause(SapHeldDraftKind? kind) => kind switch
    {
        SapHeldDraftKind.Transfer => $"DocObjectCode eq '{SapDocObjectCodes.StockTransfer}'",
        SapHeldDraftKind.Request => $"DocObjectCode eq '{SapDocObjectCodes.InventoryTransferRequest}'",
        _ => $"(DocObjectCode eq '{SapDocObjectCodes.StockTransfer}' or DocObjectCode eq '{SapDocObjectCodes.InventoryTransferRequest}')"
    };

    /// <summary>
    /// The warehouse scope as the filter takes it: trimmed, distinct, ordered. Null reads every warehouse;
    /// an empty scope is refused, because it would read as every warehouse in the OData.
    /// </summary>
    private static List<string>? ScopeOf(IReadOnlyCollection<string>? fromWarehouses)
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

        return sources;
    }

    /// <summary>
    /// The page's filters as <c> and …</c> clauses on the draft header, each one only when it narrows.
    /// Internal so the exact OData can be pinned by a unit test.
    /// </summary>
    internal static string DraftQueueFilterClauses(SapTransferDraftFilter? filter)
    {
        if (filter is null)
        {
            return string.Empty;
        }

        var clauses = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(filter.Warehouse))
        {
            var literal = EscapeODataStringLiteral(filter.Warehouse.Trim());
            clauses.Append($" and (FromWarehouse eq '{literal}' or ToWarehouse eq '{literal}')");
        }

        AppendAnyOf(clauses, "FromWarehouse", filter.FromWarehouses);
        AppendAnyOf(clauses, "ToWarehouse", filter.ToWarehouses);

        if (filter.CreatedFrom is DateTime from)
        {
            clauses.Append($" and CreationDate ge '{from:yyyy-MM-dd}'");
        }

        if (filter.CreatedTo is DateTime to)
        {
            clauses.Append($" and CreationDate le '{to:yyyy-MM-dd}'");
        }

        if (filter.AttachedOnly)
        {
            clauses.Append(" and AttachmentEntry gt 0");
        }

        if (filter.DraftNum is int draftNum)
        {
            clauses.Append($" and DocNum eq {draftNum}");
        }

        return clauses.ToString();

        static void AppendAnyOf(System.Text.StringBuilder into, string field, IReadOnlyCollection<string>? codes)
        {
            var distinct = codes?
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct is not { Count: > 0 })
            {
                return;
            }

            into.Append($" and ({string.Join(" or ", distinct.Select(code => $"{field} eq '{EscapeODataStringLiteral(code)}'"))})");
        }
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
            // Both kinds the held-transfers page reads: a transfer draft's requests carry 67, a transfer
            // request draft's 1250000001. DraftEntry is an ODRF key, unique across both.
            var filter =
                $"(ObjectType eq '{SapObjectTypes.StockTransfer}' or ObjectType eq '{SapObjectTypes.InventoryTransferRequest}')"
                + $" and ({string.Join(" or ", chunk.Select(key => $"DraftEntry eq {key}"))})";

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

    public async Task<List<SAPApprovalTemplateCondition>> GetApprovalTemplateConditionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // The Service Layer cannot filter on a template's document types (a collection), so every
        // template is read; there are under a hundred, and the caller keeps them for minutes.
        var templates = new List<SAPApprovalTemplateCondition>();
        string? url = $"ApprovalTemplates?{ApprovalTemplateConditionSelect}&$orderby=Code";
        for (var page = 0; url is not null && page < 20; page++)
        {
            var read = await ReadSapJsonAsync<SAPResponse<SAPApprovalTemplateCondition>>(
                url, $"read approval templates (page {page + 1})", cancellationToken, pageSize: 200);
            templates.AddRange(read?.Value ?? []);
            url = NextLinkPath(read?.NextLink);
        }

        return templates;
    }

    public async Task<List<SAPUserQuery>> GetUserQueriesAsync(
        IReadOnlyCollection<int> internalKeys,
        CancellationToken cancellationToken = default)
    {
        var keys = internalKeys.Where(key => key > 0).Distinct().Order().ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        var queries = new List<SAPUserQuery>(keys.Count);
        foreach (var chunk in keys.Chunk(UserQueryKeyChunkSize))
        {
            var filter = string.Join(" or ", chunk.Select(key => $"InternalKey eq {key}"));
            var page = await ReadSapJsonAsync<SAPResponse<SAPUserQuery>>(
                $"UserQueries?$filter={Uri.EscapeDataString(filter)}&{UserQuerySelect}",
                $"read {chunk.Length} user quer{(chunk.Length == 1 ? "y" : "ies")}",
                cancellationToken,
                pageSize: chunk.Length * 2);
            queries.AddRange(page?.Value ?? []);
        }

        return queries;
    }

    public async Task<List<SAPStockTransferDraft>> FindStockTransferDraftsByOrderNumberAsync(
        string orderNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return [];
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        // Measured on KEFALOS_TEST_3 (2026-11-12): the drafts set filters on U_OrderNumber.
        var filter = $"DocObjectCode eq '{SapObjectTypes.StockTransfer}' and U_OrderNumber eq '{EscapeODataStringLiteral(orderNumber.Trim())}'";
        var page = await ReadSapJsonAsync<SAPResponse<SAPStockTransferDraft>>(
            $"StockTransferDrafts?$filter={Uri.EscapeDataString(filter)}&{StockTransferDraftMarkerSelect}&$orderby=DocEntry desc&$top=20",
            $"look for stock transfer drafts marked {orderNumber}",
            cancellationToken,
            pageSize: 20);

        return page?.Value ?? [];
    }

    /// <summary>The path after the Service Layer root of an <c>odata.nextLink</c>, or null at the last page.</summary>
    private static string? NextLinkPath(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
        {
            return null;
        }

        var marker = nextLink.IndexOf("/b1s/v1/", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? nextLink[(marker + "/b1s/v1/".Length)..] : nextLink.TrimStart('/');
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

    public async Task<int?> GetNewestInventoryTransferRequestDocEntryAsync(CancellationToken cancellationToken = default)
    {
        var page = await ReadSapJsonAsync<SAPResponse<InventoryTransferRequest>>(
            $"InventoryTransferRequests?{NewestStockTransferSelect}&$orderby=DocEntry desc&$top=1",
            "read the newest inventory transfer request",
            cancellationToken,
            pageSize: 1);

        return page?.Value?.FirstOrDefault()?.DocEntry;
    }

    public async Task<List<InventoryTransferRequest>> GetInventoryTransferRequestsAfterAsync(
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

        // Requests are raised all day by depots, shops and the app itself, so the one an add made is
        // looked for among the next few rather than taken to be the next.
        var page = await ReadSapJsonAsync<SAPResponse<InventoryTransferRequest>>(
            $"InventoryTransferRequests?$filter={Uri.EscapeDataString(filter)}&{InventoryTransferRequestSelect}&$orderby=DocEntry asc&$top={CreatedTransferSearchWindow}",
            $"read the inventory transfer requests raised after {afterDocEntry}",
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
        // Document, which an inventory transfer is not. A transfer request draft is a StockTransfer too,
        // and the action makes the document its DocObjectCode names — the request, not a transfer.
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
