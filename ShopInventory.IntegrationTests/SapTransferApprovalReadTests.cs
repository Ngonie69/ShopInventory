using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Models;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer to accept the reads behind the SAP-held transfers page: the approved,
/// still-open transfer drafts and their count, filtered by source warehouse; the approval requests
/// holding them; one draft by key with its batch picks; the item flags; and the reads that recognise
/// the transfer an add produced.
/// </summary>
/// <remarks>
/// Read-only. The add — <c>StockTransferDrafts(n)/SaveDraftToDocument</c> — moves stock permanently and
/// is proven by hand against the test company, never here.
/// </remarks>
[Collection("SAP")]
public class SapTransferApprovalReadTests(SapClientFixture fixture)
{
    private static readonly SapTransferDraftFilter TransfersOnly = new() { Kind = SapHeldDraftKind.Transfer };
    private static readonly SapTransferDraftFilter RequestsOnly = new() { Kind = SapHeldDraftKind.Request };

    [SapFact]
    public async Task The_approved_draft_queue_and_its_count_are_accepted_and_are_transfers_only()
    {
        var (items, total) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, filter: TransfersOnly);

        Assert.True(total >= items.Count, $"SAP counted {total} approved drafts but returned {items.Count} on the first page.");
        // StockTransferDrafts serves every kind of draft; the filter is what keeps goods receipts out.
        Assert.All(items, item =>
        {
            Assert.Equal(SapDocObjectCodes.StockTransfer, item.DocObjectCode);
            Assert.Equal(SapDocumentStatuses.Open, item.DocumentStatus);
            Assert.Equal(SapStockTransferAuthorizationStatuses.Approved, item.AuthorizationStatus);
        });
    }

    [SapFact]
    public async Task The_queue_filters_and_counts_by_source_warehouse_and_pages_by_cursor()
    {
        var (items, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 20);
        var source = items.Select(item => item.FromWarehouse).FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        Assert.False(source is null, "None of the newest 20 approved transfer drafts names a source warehouse, so the scope filter cannot be exercised.");

        var (scoped, scopedTotal) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, fromWarehouses: [source!]);
        Assert.NotEmpty(scoped);
        Assert.All(scoped, item => Assert.Equal(source, item.FromWarehouse, ignoreCase: true));
        Assert.True(scopedTotal >= scoped.Count);

        var (either, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, filter: new SapTransferDraftFilter { Warehouse = source });
        Assert.All(either, item => Assert.True(
            string.Equals(item.FromWarehouse, source, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ToWarehouse, source, StringComparison.OrdinalIgnoreCase)));

        var cursor = items[0].DocEntry;
        var (below, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, beforeDocEntry: cursor);
        Assert.All(below, item => Assert.True(item.DocEntry < cursor));
    }

    /// <summary>
    /// The page's filters are clauses on the draft header; SAP must accept each one and apply it to the
    /// rows. Built from the newest drafts themselves, so every filter is known to match at least one.
    /// </summary>
    [SapFact]
    public async Task The_page_filters_are_accepted_and_narrow_the_rows()
    {
        var (items, total) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 20);
        Assert.False(items.Count == 0, "SAP holds no approved transfer drafts, so the filters cannot be exercised.");
        var sample = items.First(item => !string.IsNullOrWhiteSpace(item.FromWarehouse) && !string.IsNullOrWhiteSpace(item.ToWarehouse));

        var (byRoute, routeTotal) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, filter: new SapTransferDraftFilter
        {
            FromWarehouses = [sample.FromWarehouse!],
            ToWarehouses = [sample.ToWarehouse!]
        });
        Assert.NotEmpty(byRoute);
        Assert.All(byRoute, item =>
        {
            Assert.Equal(sample.FromWarehouse, item.FromWarehouse, ignoreCase: true);
            Assert.Equal(sample.ToWarehouse, item.ToWarehouse, ignoreCase: true);
        });
        Assert.True(routeTotal <= total);

        var created = DateTime.Parse(sample.CreationDate!, System.Globalization.CultureInfo.InvariantCulture).Date;
        var (sameDay, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 20, filter: new SapTransferDraftFilter
        {
            CreatedFrom = created,
            CreatedTo = created
        });
        Assert.Contains(sameDay, item => item.DocEntry == sample.DocEntry);
        Assert.All(sameDay, item => Assert.Equal(created, DateTime.Parse(item.CreationDate!, System.Globalization.CultureInfo.InvariantCulture).Date));

        var (byNumber, numberTotal) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, filter: new SapTransferDraftFilter
        {
            DraftNum = sample.DocNum
        });
        Assert.Equal(sample.DocEntry, Assert.Single(byNumber).DocEntry);
        Assert.Equal(1, numberTotal);

        var (attached, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, filter: new SapTransferDraftFilter
        {
            AttachedOnly = true
        });
        Assert.All(attached, item => Assert.True(item.AttachmentEntry > 0));
    }

    [SapFact]
    public async Task A_draft_reads_back_by_key_with_its_batches_requests_and_item_flags()
    {
        var (items, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 10, filter: TransfersOnly);
        Assert.False(items.Count == 0, "SAP holds no approved transfer drafts, so the detail reads cannot be exercised.");

        var requests = await fixture.Client.GetStockTransferApprovalRequestsAsync(items.Select(item => item.DocEntry).ToList(), withLines: true);
        Assert.All(requests, request => Assert.Equal(SapObjectTypes.StockTransfer, request.ObjectType));
        Assert.Contains(requests, request => items.Any(item => item.DocEntry == request.DraftEntry));

        var draft = await fixture.Client.GetStockTransferDraftAsync(items[0].DocEntry);
        Assert.NotNull(draft);
        Assert.NotEmpty(draft.StockTransferLines!);

        var management = await fixture.Client.GetItemManagementAsync(draft.StockTransferLines!.Select(line => line.ItemCode!).ToList());
        Assert.NotEmpty(management);

        // The by-key read is the one that carries the picks: a batch-managed line shows its batches.
        var batched = items
            .Select(item => item.DocEntry)
            .Take(5)
            .Select(entry => fixture.Client.GetStockTransferDraftAsync(entry).GetAwaiter().GetResult())
            .SelectMany(found => found?.StockTransferLines ?? [])
            .Any(line => line.BatchNumbers is { Count: > 0 });
        Assert.True(batched, "None of the five newest approved drafts read back with a batch pick; the by-key read should carry them.");
    }

    /// <summary>
    /// Transfer request drafts sit in the same set under their own object code. This is the read that
    /// settles what SAP calls it: the filter and the page's kind check both take it to be
    /// <c>1250000001</c>, as the transfer's is <c>67</c>. An empty answer is not proof either way.
    /// </summary>
    [SapFact]
    public async Task The_request_draft_queue_is_accepted_and_holds_only_request_drafts()
    {
        var (items, total) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5, filter: RequestsOnly);

        Assert.True(total >= items.Count, $"SAP counted {total} approved request drafts but returned {items.Count} on the first page.");
        Assert.All(items, item =>
        {
            Assert.Equal(SapDocObjectCodes.InventoryTransferRequest, item.DocObjectCode);
            Assert.Equal(SapDocumentStatuses.Open, item.DocumentStatus);
            Assert.Equal(SapStockTransferAuthorizationStatuses.Approved, item.AuthorizationStatus);
        });

        if (items.Count > 0)
        {
            // Their approval requests carry the request's object type, which the join reads as well.
            var requests = await fixture.Client.GetStockTransferApprovalRequestsAsync(items.Select(item => item.DocEntry).ToList());
            Assert.All(requests, request => Assert.Equal(SapObjectTypes.InventoryTransferRequest, request.ObjectType));
        }
    }

    /// <summary>The kind switch shows the two totals; together they must be the count of both kinds read at once.</summary>
    [SapFact]
    public async Task Both_kinds_count_as_the_sum_of_each()
    {
        var transfers = await fixture.Client.CountApprovedStockTransferDraftsAsync(filter: TransfersOnly);
        var requests = await fixture.Client.CountApprovedStockTransferDraftsAsync(filter: RequestsOnly);
        var both = await fixture.Client.CountApprovedStockTransferDraftsAsync();

        Assert.Equal(transfers + requests, both);
    }

    [SapFact]
    public async Task The_reads_that_recognise_a_created_request_are_accepted()
    {
        var newest = await fixture.Client.GetNewestInventoryTransferRequestDocEntryAsync();
        Assert.NotNull(newest);

        var request = await fixture.Client.GetInventoryTransferRequestByDocEntryAsync(newest.Value);
        Assert.NotNull(request);

        var after = await fixture.Client.GetInventoryTransferRequestsAfterAsync(newest.Value - 5, request.FromWarehouse, request.ToWarehouse);
        Assert.Contains(after, found => found.DocEntry == newest.Value);
    }

    [SapFact]
    public async Task The_reads_that_recognise_a_created_transfer_are_accepted()
    {
        var newest = await fixture.Client.GetNewestStockTransferDocEntryAsync();
        Assert.NotNull(newest);

        var transfer = await fixture.Client.GetInventoryTransferByDocEntryAsync(newest.Value);
        Assert.NotNull(transfer);

        var after = await fixture.Client.GetStockTransfersAfterAsync(newest.Value - 5, transfer.FromWarehouse, transfer.ToWarehouse);
        Assert.Contains(after, found => found.DocEntry == newest.Value);
    }

    /// <summary>
    /// Every active SAP template on inventory transfers must be one the app can answer for a route:
    /// a conversion on a route a template it cannot read covers would post past SAP's approval. Run it
    /// against production (SAP__CompanyDB) before relying on the conversion rule there.
    /// </summary>
    [SapFact]
    public async Task Every_active_transfer_template_condition_can_be_answered_for_a_route()
    {
        var templates = (await fixture.Client.GetApprovalTemplateConditionsAsync())
            .Where(template => template.IsActive == "tYES"
                && (template.ApprovalTemplateDocuments ?? []).Any(document => document.DocumentType == "atdtInventoryTransfer"))
            .ToList();
        Assert.False(templates.Count == 0, "SAP holds no active approval template on inventory transfers.");

        var queries = (await fixture.Client.GetUserQueriesAsync(
                templates.SelectMany(template => template.ApprovalTemplateQueries ?? []).Select(query => query.QueryID).ToList()))
            .GroupBy(query => query.InternalKey)
            .ToDictionary(group => group.Key, group => group.ToList());

        var unreadable = templates
            .Select(template => SapTransferApprovalRoutes.Compile(template, queries))
            .Where(template => template.Unreadable.Count > 0)
            .Select(template => $"#{template.Code} '{template.Name}': {string.Join("; ", template.Unreadable)}")
            .ToList();

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The reading of a template checked against SAP's own decisions: every transfer draft SAP held
    /// under a template — read here from the approved queue and the approval requests behind it — must
    /// be on a route the app says that same template covers. A miss means a conversion on that route
    /// would post past SAP's approval.
    /// </summary>
    /// <remarks>
    /// Only the last year of SAP's decisions is compared: templates get re-pointed. On KEFALOS_TEST_3
    /// (read 2026-11-12) template #69 "CMFS Stock Transfers" held transfers into CMFS from Aug to Oct
    /// 2022 and only transfers out of CMFS since; its 2022 drafts are still in the approved queue, and
    /// they are the three of 84 the template as it reads today does not cover.
    /// </remarks>
    [SapFact]
    public async Task Every_transfer_SAP_held_is_on_a_route_its_template_covers_here()
    {
        var templates = (await fixture.Client.GetApprovalTemplateConditionsAsync())
            .Where(template => template.IsActive == "tYES"
                && (template.ApprovalTemplateDocuments ?? []).Any(document => document.DocumentType == "atdtInventoryTransfer"))
            .ToList();
        var queries = (await fixture.Client.GetUserQueriesAsync(
                templates.SelectMany(template => template.ApprovalTemplateQueries ?? []).Select(query => query.QueryID).ToList()))
            .GroupBy(query => query.InternalKey)
            .ToDictionary(group => group.Key, group => group.ToList());
        var compiled = templates.ToDictionary(template => template.Code, template => SapTransferApprovalRoutes.Compile(template, queries));

        var (drafts, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 100, filter: TransfersOnly);
        var byEntry = drafts.ToDictionary(draft => draft.DocEntry);
        var requests = await fixture.Client.GetStockTransferApprovalRequestsAsync(byEntry.Keys.ToList());
        var newest = requests.Select(request => DateTime.TryParse(request.CreationDate, out var created) ? created : DateTime.MinValue).DefaultIfEmpty().Max();
        var since = newest.AddYears(-1);

        var checkedCount = 0;
        var misses = new List<string>();
        foreach (var request in requests)
        {
            if (request.DraftEntry is not int draftEntry || !byEntry.TryGetValue(draftEntry, out var draft) ||
                request.ApprovalTemplatesID is not int templateCode || !compiled.TryGetValue(templateCode, out var template))
            {
                continue; // a template since switched off says nothing about today's routes
            }

            if (!DateTime.TryParse(request.CreationDate, out var held) || held < since)
            {
                continue;
            }

            checkedCount++;
            if (!template.Holds(draft.FromWarehouse, draft.ToWarehouse))
                misses.Add($"draft {draft.DocNum} {draft.FromWarehouse}->{draft.ToWarehouse} held by #{template.Code} '{template.Name}'");
        }

        Assert.True(checkedCount > 0, "No approved transfer draft is held by a template that is still active, so nothing was compared.");
        Assert.True(misses.Count == 0, $"{misses.Count} of {checkedCount}:{Environment.NewLine}{string.Join(Environment.NewLine, misses)}");
    }

    [SapFact]
    public async Task Transfer_drafts_are_looked_up_by_their_order_number_marker()
    {
        // No request has DocEntry 0, so nothing carries ITR-0: SAP must accept the filter and answer empty.
        var drafts = await fixture.Client.FindStockTransferDraftsByOrderNumberAsync("ITR-0");

        Assert.Empty(drafts);
    }
}
