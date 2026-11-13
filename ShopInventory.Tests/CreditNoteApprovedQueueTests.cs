using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;
using ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The open queues' approved half is only the approved requests whose draft SAP still holds. SAP leaves a
/// request Approved after its draft is added from the B1 client, so on production the Approved queue was
/// 6,774 requests of which 169 could still be added; the rest showed as "The draft is closed or cancelled
/// in SAP" and buried the ones somebody could act on.
/// </summary>
public sealed class CreditNoteApprovedQueueTests
{
    private static readonly SAPUser Manager = new() { InternalKey = 1, UserCode = "manager", UserName = "Site Manager" };

    [Fact]
    public async Task The_approved_queue_reads_only_the_requests_over_drafts_sap_still_holds()
    {
        // 3120 and 3118 are over held drafts; 3119 and 3117 were added long ago, so SAP holds no draft for them.
        var sap = new QueueSap(
            approved: [Approved(3120, 900), Approved(3119, 800), Approved(3118, 901), Approved(3117, 801)],
            pending: []);
        var handler = ListHandler(sap, new FakeAlreadyCreditedIndex { HeldDrafts = new HashSet<int> { 900, 901 } });

        var result = (await handler.Handle(new GetCreditNoteApprovalsQuery("approved", 1, 25), CancellationToken.None)).Value;

        Assert.Equal(new[] { 3120, 3118 }, result.Items.Select(item => item.Code).ToArray());
        Assert.Equal(2, result.TotalCount);
        Assert.Null(result.NextCursor);

        // Never SAP's paged read of every approved request: that is the history the queue leaves out.
        Assert.Equal(0, sap.LiveReads);
        Assert.Equal(new[] { 900, 901 }, sap.AskedForDrafts!.Order().ToArray());
        Assert.Equal(new[] { SapApprovalRequestStatuses.Approved }, sap.AskedForDraftStatuses!.ToArray());
    }

    [Fact]
    public async Task The_open_queue_merges_live_pending_rows_with_held_approved_ones_newest_first()
    {
        var sap = new QueueSap(
            approved: [Approved(3125, 905), Approved(3122, 902), Approved(3111, 700)],
            pending: [Pending(3124, 904), Pending(3123, 903), Pending(3121, 901)]);
        var handler = ListHandler(sap, new FakeAlreadyCreditedIndex { HeldDrafts = new HashSet<int> { 901, 902, 903, 904, 905 } });

        var result = (await handler.Handle(new GetCreditNoteApprovalsQuery("open", 1, 25), CancellationToken.None)).Value;

        Assert.Equal(new[] { 3125, 3124, 3123, 3122, 3121 }, result.Items.Select(item => item.Code).ToArray());
        Assert.Equal(5, result.TotalCount);

        // The live read asks SAP for pending only; the approved half came from the held drafts.
        Assert.Equal(new[] { SapApprovalRequestStatuses.Pending }, sap.LiveStatuses!.ToArray());
    }

    /// <summary>
    /// Paging across both halves must neither repeat a row nor skip one. When the live read was cut short,
    /// SAP may hold pending rows below it not read yet, so no approved row below the lowest one read is
    /// shown ahead of them.
    /// </summary>
    [Fact]
    public async Task Cursor_pages_walk_both_halves_without_a_gap_or_a_repeat()
    {
        var pending = new[] { 20, 18, 17, 12, 11, 5 }.Select(code => Pending(code, 1000 + code)).ToList();
        var approved = new[] { 19, 16, 15, 14, 3 }.Select(code => Approved(code, 1000 + code)).ToList();
        var held = pending.Concat(approved).Select(request => request.DraftEntry!.Value).ToHashSet();
        var sap = new QueueSap(approved, pending);
        var handler = ListHandler(sap, new FakeAlreadyCreditedIndex { HeldDrafts = held });

        var seen = new List<int>();
        int? cursor = null;
        var pages = 0;
        do
        {
            var page = (await handler.Handle(new GetCreditNoteApprovalsQuery("open", pages + 1, 3, BeforeCode: cursor), CancellationToken.None)).Value;
            Assert.Equal(11, page.TotalCount);
            seen.AddRange(page.Items.Select(item => item.Code));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(new[] { 20, 19, 18, 17, 16, 15, 14, 12, 11, 5, 3 }, seen.ToArray());
    }

    [Fact]
    public async Task An_approved_request_already_credited_is_hidden_from_the_merged_queue_and_its_count()
    {
        var sap = new QueueSap(
            approved: [Approved(3122, 902), Approved(3121, 901)],
            pending: [Pending(3123, 903)]);
        var index = new FakeAlreadyCreditedIndex(Credited(3122)) { HeldDrafts = new HashSet<int> { 901, 902, 903 } };

        var open = (await ListHandler(sap, index).Handle(new GetCreditNoteApprovalsQuery("open", 1, 25), CancellationToken.None)).Value;
        var approved = (await ListHandler(sap, index).Handle(new GetCreditNoteApprovalsQuery("approved", 1, 25), CancellationToken.None)).Value;

        Assert.Equal(new[] { 3123, 3121 }, open.Items.Select(item => item.Code).ToArray());
        Assert.Equal(2, open.TotalCount);
        Assert.Equal(new[] { 3121 }, approved.Items.Select(item => item.Code).ToArray());
        Assert.Equal(1, approved.TotalCount);
    }

    [Fact]
    public async Task The_callers_stages_and_narrowing_go_to_the_held_draft_read()
    {
        var sap = new QueueSap(approved: [Approved(3120, 900)], pending: []);
        var handler = ListHandler(sap, new FakeAlreadyCreditedIndex { HeldDrafts = new HashSet<int> { 900 } }, FixedStageScope.Stages("Factory Washbay", 44));
        var filter = new CreditNoteApprovalRequestFilter(new DateTime(2026, 11, 1), null, 31);

        await handler.Handle(new GetCreditNoteApprovalsQuery("approved", 1, 25, Filter: filter), CancellationToken.None);

        Assert.Equal(new[] { 44 }, sap.AskedForDraftStages!.ToArray());
        Assert.Equal(filter, sap.AskedForDraftFilter);
    }

    /// <summary>When SAP cannot say which drafts it holds, the queue is read as it always was, history included.</summary>
    [Fact]
    public async Task An_index_that_cannot_say_which_drafts_are_held_falls_back_to_the_plain_read()
    {
        var sap = new QueueSap(approved: [Approved(3120, 900), Approved(3119, 800)], pending: []);
        var handler = ListHandler(sap, new FakeAlreadyCreditedIndex { Fails = true });

        var result = (await handler.Handle(new GetCreditNoteApprovalsQuery("approved", 1, 25), CancellationToken.None)).Value;

        Assert.Equal(new[] { 3120, 3119 }, result.Items.Select(item => item.Code).ToArray());
        Assert.Equal(1, sap.LiveReads);
        Assert.Null(sap.AskedForDrafts);
    }

    /// <summary>"All requests" is the history, so it keeps every approved request and never asks about held drafts.</summary>
    [Fact]
    public async Task All_requests_still_shows_every_approved_request()
    {
        var sap = new QueueSap(approved: [Approved(3120, 900), Approved(3119, 800)], pending: []);
        var handler = ListHandler(sap, new FakeAlreadyCreditedIndex { HeldDrafts = new HashSet<int> { 900 } });

        var result = (await handler.Handle(new GetCreditNoteApprovalsQuery("all", 1, 25), CancellationToken.None)).Value;

        Assert.Equal(new[] { 3120, 3119 }, result.Items.Select(item => item.Code).ToArray());
        Assert.Null(sap.AskedForDrafts);
    }

    [Fact]
    public async Task The_approved_tile_counts_what_the_approved_queue_shows()
    {
        var sap = new QueueSap(
            approved: [Approved(3122, 902), Approved(3121, 901), Approved(3119, 800), Approved(3118, 801)],
            pending: [Pending(3123, 903)],
            approvedCount: 4,
            pendingCount: 1);
        var index = new FakeAlreadyCreditedIndex(Credited(3122)) { HeldDrafts = new HashSet<int> { 901, 902, 903 } };

        var counts = (await CountsHandler(sap, index).Handle(new GetCreditNoteApprovalCountsQuery(), CancellationToken.None)).Value;

        Assert.Equal(1, counts.Pending);
        Assert.Equal(1, counts.Approved);
        Assert.DoesNotContain(SapApprovalRequestStatuses.Approved, sap.CountedStatuses);
    }

    [Fact]
    public async Task The_approved_tile_falls_back_to_sap_count_when_the_held_drafts_are_unknown()
    {
        var sap = new QueueSap(approved: [], pending: [], approvedCount: 6774, pendingCount: 111);

        var counts = (await CountsHandler(sap, new FakeAlreadyCreditedIndex()).Handle(new GetCreditNoteApprovalCountsQuery(), CancellationToken.None)).Value;

        Assert.Equal(111, counts.Pending);
        Assert.Equal(6774, counts.Approved);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static GetCreditNoteApprovalsHandler ListHandler(
        QueueSap sap,
        FakeAlreadyCreditedIndex index,
        ICreditNoteApprovalStageScope? scope = null) =>
        new(
            sap.AsClient(),
            FakeSapApprovalLookups.WithStage(4, "Finance review", [Manager], 1),
            scope ?? FixedStageScope.EveryStage,
            index,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCreditNoteApprovalsHandler>.Instance);

    private static GetCreditNoteApprovalCountsHandler CountsHandler(QueueSap sap, FakeAlreadyCreditedIndex index) =>
        new(
            sap.AsClient(),
            FixedStageScope.EveryStage,
            index,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCreditNoteApprovalCountsHandler>.Instance);

    private static SAPApprovalRequest Pending(int code, int draftEntry) => Request(code, draftEntry, SapApprovalRequestStatuses.Pending);

    private static SAPApprovalRequest Approved(int code, int draftEntry) => Request(code, draftEntry, SapApprovalRequestStatuses.Approved);

    private static SAPApprovalRequest Request(int code, int draftEntry, string status) => new()
    {
        Code = code,
        ObjectType = SapObjectTypes.CreditNote,
        Status = status,
        DraftEntry = draftEntry,
        CurrentStage = 4,
        OriginatorID = 12,
        ApprovalTemplatesID = 7,
        CreationDate = "2026-11-01T00:00:00Z"
    };

    private static AlreadyCreditedRequest Credited(int code) =>
        new(code, SapApprovalRequestStatuses.Approved, 4, new DateTime(2026, 11, 1), 12, new SAPCreditNote
        {
            DocEntry = 70310,
            DocNum = 61455,
            CardCode = "C00477",
            NumAtCard = "800123",
            DocTotal = 10m,
            CreationDate = "2026-11-02T00:00:00Z",
            Cancelled = SapYesNo.No,
            CancelStatus = "csNo"
        });

    /// <summary>
    /// SAP's credit memo requests as the client reads them: the live paged read (Code desc, below the
    /// cursor, as many as asked), the read by draft, the drafts themselves, and the counts.
    /// </summary>
    private sealed class QueueSap(
        List<SAPApprovalRequest> approved,
        List<SAPApprovalRequest> pending,
        int? approvedCount = null,
        int? pendingCount = null)
    {
        public int LiveReads { get; private set; }
        public IReadOnlyCollection<string>? LiveStatuses { get; private set; }
        public IReadOnlyCollection<int>? AskedForDrafts { get; private set; }
        public IReadOnlyCollection<string>? AskedForDraftStatuses { get; private set; }
        public IReadOnlyCollection<int>? AskedForDraftStages { get; private set; }
        public CreditNoteApprovalRequestFilter? AskedForDraftFilter { get; private set; }
        public List<string> CountedStatuses { get; } = [];

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetCreditNoteApprovalRequestsAsync) => Live(args!),
            nameof(ISAPServiceLayerClient.GetCreditNoteApprovalRequestsForDraftsAsync) => ForDrafts(args!),
            nameof(ISAPServiceLayerClient.GetCreditNoteDraftsAsync) => Task.FromResult(new List<SAPCreditNote>()),
            nameof(ISAPServiceLayerClient.CountCreditNoteApprovalRequestsAsync) => Count(args!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private IEnumerable<SAPApprovalRequest> In(IReadOnlyCollection<string> statuses) =>
            approved.Concat(pending).Where(request => statuses.Contains(request.Status!));

        private Task<(List<SAPApprovalRequest> Items, int TotalCount)> Live(object?[] args)
        {
            LiveReads++;
            var statuses = (IReadOnlyCollection<string>)args[0]!;
            LiveStatuses = statuses;
            var size = (int)args[2]!;
            var cursor = (int?)args[3];
            var all = In(statuses).OrderByDescending(request => request.Code).ToList();
            var page = all.Where(request => cursor is not int below || request.Code < below).Take(size).ToList();
            return Task.FromResult((page, all.Count));
        }

        private Task<List<SAPApprovalRequest>> ForDrafts(object?[] args)
        {
            AskedForDraftStatuses = (IReadOnlyCollection<string>)args[0]!;
            AskedForDrafts = (IReadOnlyCollection<int>)args[1]!;
            AskedForDraftStages = (IReadOnlyCollection<int>?)args[2];
            AskedForDraftFilter = (CreditNoteApprovalRequestFilter?)args[3];

            // In draft order, as the batched reads come back, not in Code order.
            return Task.FromResult(In(AskedForDraftStatuses)
                .Where(request => AskedForDrafts.Contains(request.DraftEntry ?? 0))
                .OrderBy(request => request.DraftEntry)
                .ToList());
        }

        private Task<int> Count(object?[] args)
        {
            var statuses = (IReadOnlyCollection<string>)args[0]!;
            CountedStatuses.AddRange(statuses);
            return Task.FromResult(statuses.Contains(SapApprovalRequestStatuses.Approved)
                ? approvedCount ?? approved.Count
                : statuses.Contains(SapApprovalRequestStatuses.Pending) ? pendingCount ?? pending.Count : 0);
        }
    }
}
