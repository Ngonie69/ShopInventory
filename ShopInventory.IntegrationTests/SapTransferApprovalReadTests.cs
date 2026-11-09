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
    [SapFact]
    public async Task The_approved_draft_queue_and_its_count_are_accepted_and_are_transfers_only()
    {
        var (items, total) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 5);

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
        var (items, _) = await fixture.Client.GetApprovedStockTransferDraftsAsync(1, 10);
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
}
