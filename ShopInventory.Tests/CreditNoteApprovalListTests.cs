using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The approvals list: SAP's requests joined to the drafts they hold, labelled through the lookups,
/// and each row told whether this app may decide or add it.
/// </summary>
/// <remarks>
/// The flags are the point. A row that says "Approve" when SAP's stage does not list the service
/// approver sends a manager into a SAP refusal; a row that hides "Add" on an approved open draft
/// strands the credit memo. Both are decided in <see cref="CreditNoteApprovalProjection"/>, and the
/// list and the detail share it, so these assertions hold for the drawer too.
/// </remarks>
public sealed class CreditNoteApprovalListTests
{
    private static readonly SAPUser Manager = new() { InternalKey = 1, UserCode = "manager", UserName = "Site Manager" };
    private static readonly SAPUser Clerk = new() { InternalKey = 12, UserCode = "clerk", UserName = "Front Clerk" };
    private static readonly SAPUser Finance = new() { InternalKey = 9, UserCode = "finmgr", UserName = "Finance Manager" };
    private static readonly Guid WashBayUserId = Guid.Parse("5a5b0000-0000-0000-0000-000000000b01");

    [Fact]
    public async Task Rows_join_the_draft_and_name_the_people_template_and_stage()
    {
        var request = Pending(code: 3110, draftEntry: 88123, stage: 4, originator: 12, template: 7);
        var draft = Draft(88123, "SPA059", "Spar Avondale", 245.60m, attachmentEntry: 5021);
        var sap = new RecordingSapClient([request], total: 1, [draft]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1, 9));

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery(null, 1, 25), CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        var response = result.Value;
        Assert.Equal(1, response.TotalCount);
        Assert.Equal("open", response.Status);

        var row = Assert.Single(response.Items);
        Assert.Equal(3110, row.Code);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(88123, row.DraftEntry);
        Assert.Equal("Spar Avondale", row.CardName);
        Assert.Equal(245.60m, row.DocTotal);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), row.DocDate);
        Assert.Equal("clerk", row.OriginatorUserCode);
        Assert.Equal("Front Clerk", row.OriginatorName);
        Assert.Equal("Returns", row.TemplateName);
        Assert.Equal("Finance review", row.StageName);
        Assert.Equal("Pending", row.DraftAuthorizationStatus);
        Assert.True(row.HasAttachment);
        Assert.True(row.DraftIsOpen);
        Assert.True(row.CanDecide);
        Assert.False(row.CanAdd);
        Assert.Null(row.StatusNote);

        // The default filter asks SAP for the two states somebody can still act on, and the drafts in one batch.
        Assert.Equal(
            new[] { SapApprovalRequestStatuses.Pending, SapApprovalRequestStatuses.Approved },
            sap.RequestedStatuses!.ToArray());
        Assert.Equal(new[] { 88123 }, sap.RequestedDraftEntries!.ToArray());
    }

    [Fact]
    public async Task A_pending_request_whose_stage_does_not_list_the_service_approver_says_so()
    {
        var request = Pending(3110, 88123, stage: 4, originator: 12, template: 7);
        var sap = new RecordingSapClient([request], 1, [Draft(88123, "SPA059", "Spar Avondale", 10m, null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 9));

        var row = Assert.Single((await handler.Handle(new GetCreditNoteApprovalsQuery("pending", 1, 25), CancellationToken.None)).Value.Items);

        Assert.False(row.CanDecide);
        Assert.Equal("SAP stage 'Finance review' does not list manager as an approver.", row.StatusNote);
    }

    [Fact]
    public async Task An_approved_open_draft_can_be_added_and_a_closed_one_cannot()
    {
        var sap = new RecordingSapClient(
            [Approved(3111, 88124), Approved(3112, 88125)],
            2,
            [
                Draft(88124, "SPA059", "Spar Avondale", 10m, null, authorization: SapDocumentAuthorizationStatuses.Approved),
                Draft(88125, "SPA059", "Spar Avondale", 10m, null, authorization: SapDocumentAuthorizationStatuses.Approved, documentStatus: SapDocumentStatuses.Closed)
            ]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1));

        var rows = (await handler.Handle(new GetCreditNoteApprovalsQuery("approved", 1, 25), CancellationToken.None)).Value.Items;

        Assert.Equal("Approved", rows[0].Status);
        Assert.True(rows[0].CanAdd);
        Assert.False(rows[0].CanDecide);
        Assert.Null(rows[0].StatusNote);

        Assert.False(rows[1].CanAdd);
        Assert.False(rows[1].DraftIsOpen);
        Assert.Equal("The draft is closed or cancelled in SAP.", rows[1].StatusNote);
    }

    [Fact]
    public async Task An_approved_request_whose_draft_sap_left_pending_cannot_be_added_and_says_why()
    {
        var sap = new RecordingSapClient([Approved(3111, 88124)], 1, [Draft(88124, "TMP065", "TM Newlands USD", 265.65m, null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1));

        var row = Assert.Single((await handler.Handle(new GetCreditNoteApprovalsQuery("approved", 1, 25), CancellationToken.None)).Value.Items);

        Assert.Equal("Approved", row.Status);
        Assert.Equal("Pending", row.DraftAuthorizationStatus);
        Assert.False(row.CanAdd);
        Assert.Equal(
            "SAP marked the request Approved but left the draft Pending, so it cannot be added. Ask the originator to raise it again in SAP.",
            row.StatusNote);
    }

    [Fact]
    public async Task A_generated_request_names_its_credit_note_and_a_missing_draft_is_reported_not_dropped()
    {
        var generated = new SAPApprovalRequest
        {
            Code = 3113, ObjectType = "14", Status = SapApprovalRequestStatuses.Generated, DraftEntry = 88126, ObjectEntry = 9001
        };
        var sap = new RecordingSapClient([generated, Approved(3114, 88127)], 2, []);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1));

        var rows = (await handler.Handle(new GetCreditNoteApprovalsQuery("all", 1, 25), CancellationToken.None)).Value.Items;

        Assert.Equal(2, rows.Count);
        Assert.Equal("Generated", rows[0].Status);
        Assert.Equal(9001, rows[0].CreditNoteDocEntry);
        Assert.Equal("Added as credit note DocEntry 9001.", rows[0].StatusNote);

        Assert.False(rows[1].CanAdd);
        Assert.Equal("The draft behind this request no longer exists in SAP.", rows[1].StatusNote);
        Assert.Equal(6, sap.RequestedStatuses!.Count);
    }

    [Fact]
    public async Task A_failed_lookup_costs_the_row_its_label_not_the_list_its_answer()
    {
        var request = Pending(3110, 88123, stage: 4, originator: 12, template: 7);
        var sap = new RecordingSapClient([request], 1, [Draft(88123, "SPA059", "Spar Avondale", 10m, null)]);
        var lookups = LookupsWithStage(4, "Finance review", 1);
        lookups.UsersUnavailable = true;
        var handler = Handler(sap, lookups);

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery(null, 1, 25), CancellationToken.None);

        Assert.False(result.IsError);
        var row = Assert.Single(result.Value.Items);
        Assert.Null(row.OriginatorUserCode);
        Assert.Equal(12, row.OriginatorId);
        // Without a service approver SAP can be asked as, a decision is off the table and the row says why.
        Assert.False(row.CanDecide);
        Assert.Contains("SAP has no user 'manager'", row.StatusNote);
    }

    [Fact]
    public void The_status_filter_maps_to_saps_literals()
    {
        Assert.Equal(new[] { SapApprovalRequestStatuses.Pending }, CreditNoteApprovalStatusFilters.ToSapStatuses("pending").ToArray());
        Assert.Equal(new[] { SapApprovalRequestStatuses.Approved }, CreditNoteApprovalStatusFilters.ToSapStatuses("Approved").ToArray());
        Assert.Equal(new[] { SapApprovalRequestStatuses.NotApproved }, CreditNoteApprovalStatusFilters.ToSapStatuses("rejected").ToArray());
        Assert.True(CreditNoteApprovalStatusFilters.IsKnown("rejected"));
        Assert.Equal(
            new[] { SapApprovalRequestStatuses.Pending, SapApprovalRequestStatuses.Approved },
            CreditNoteApprovalStatusFilters.ToSapStatuses(null).ToArray());
        Assert.Equal(6, CreditNoteApprovalStatusFilters.ToSapStatuses("all").Count);
        Assert.Equal("open", CreditNoteApprovalStatusFilters.Normalise(" "));

        Assert.True(CreditNoteApprovalStatusFilters.IsKnown(null));
        Assert.True(CreditNoteApprovalStatusFilters.IsKnown("OPEN"));
        Assert.False(CreditNoteApprovalStatusFilters.IsKnown("bogus"));
    }

    [Fact]
    public async Task Sap_disabled_is_refused_before_anything_is_read()
    {
        var sap = new RecordingSapClient([], 0, []);
        var handler = new GetCreditNoteApprovalsHandler(
            sap.AsClient(),
            LookupsWithStage(4, "Finance review", 1),
            FixedStageScope.EveryStage,
            FakeAlreadyCreditedIndex.Empty,
            Options.Create(new SAPSettings { Enabled = false }),
            NullLogger<GetCreditNoteApprovalsHandler>.Instance);

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery(null, 1, 25), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("CreditNoteApproval.SapDisabled", result.FirstError.Code);
        Assert.Null(sap.RequestedStatuses);
    }

    /// <summary>
    /// The wash bay's queue is filtered in SAP, not after it. Dropping other stages' rows from a page of
    /// the whole queue would leave short pages and a count promising rows that never come.
    /// </summary>
    [Fact]
    public async Task A_stage_scoped_caller_asks_sap_for_its_own_stages_only()
    {
        var request = Pending(code: 3110, draftEntry: 88123, stage: 4, originator: 12, template: 7);
        var draft = Draft(88123, "SPA059", "Spar Avondale", 10m, attachmentEntry: null);
        var scoped = new RecordingSapClient([request], total: 1, [draft]);
        var scope = FixedStageScope.Stages("Wash Bay Approvals", 4);

        var result = await Handler(scoped, LookupsWithStage(4, "Wash Bay Approvals", 1), scope)
            .Handle(new GetCreditNoteApprovalsQuery(null, 1, 25, CallerUserId: WashBayUserId), CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.Equal([4], scoped.RequestedStageCodes!);
        Assert.Equal(WashBayUserId, Assert.Single(scope.AskedFor));

        // The negative control: a caller who sees every stage sends no stage filter at all.
        var unscoped = new RecordingSapClient([request], total: 1, [draft]);
        await Handler(unscoped, LookupsWithStage(4, "Wash Bay Approvals", 1))
            .Handle(new GetCreditNoteApprovalsQuery(null, 1, 25, CallerUserId: Guid.NewGuid()), CancellationToken.None);
        Assert.NotNull(unscoped.RequestedStatuses);
        Assert.Null(unscoped.RequestedStageCodes);
    }

    [Fact]
    public async Task A_scope_that_cannot_be_resolved_is_refused_before_the_queue_is_read()
    {
        var sap = new RecordingSapClient([], 0, []);
        var scope = new FixedStageScope(Errors.CreditNoteApproval.StageScopeUnresolved("'Wash Bay Approvals'"));

        var result = await Handler(sap, LookupsWithStage(4, "Finance review", 1), scope)
            .Handle(new GetCreditNoteApprovalsQuery(null, 1, 25, CallerUserId: WashBayUserId), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.StageScopeUnresolved", result.FirstError.Code);
        Assert.Null(sap.RequestedStatuses);
    }

    /// <summary>
    /// A production request: SAP still held the draft Pending, but the credit had been keyed straight
    /// into SAP that afternoon. The open queues leave it out, as SAP leaves out a draft once added, and
    /// the count says how many are left.
    /// </summary>
    [Fact]
    public async Task A_draft_already_credited_is_hidden_from_the_open_queues_and_their_count()
    {
        var sap = new RecordingSapClient(
            [Pending(3110, 88123, stage: 4, originator: 12, template: 7), Pending(3109, 88122, stage: 4, originator: 12, template: 7)],
            total: 2,
            [Draft(88123, "C00477", "Corner Store USD", 412.65m, null), Draft(88122, "C00478", "Hillside Superette", 96.25m, null)]);
        var index = new FakeAlreadyCreditedIndex(Credited(3110));

        foreach (var queue in new[] { "open", "pending" })
        {
            var result = await Handler(sap, LookupsWithStage(4, "Finance review", 1), index: index)
                .Handle(new GetCreditNoteApprovalsQuery(queue, 1, 25), CancellationToken.None);

            var row = Assert.Single(result.Value.Items);
            Assert.Equal(3109, row.Code);
            Assert.Null(row.AlreadyCredited);
            Assert.Equal(1, result.Value.TotalCount);
        }

        // The hidden row's draft is never read: nothing on the page needs it.
        Assert.Equal(new[] { 88122 }, sap.RequestedDraftEntries!.ToArray());
    }

    /// <summary>"All requests" is where an already-credited draft can still be found, flagged, to clear it.</summary>
    [Fact]
    public async Task All_requests_shows_a_draft_already_credited_flagged_with_reject_only()
    {
        var sap = new RecordingSapClient([Pending(3110, 88123, stage: 4, originator: 12, template: 7)], 1, [Draft(88123, "C00477", "Corner Store USD", 412.65m, null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1), index: new FakeAlreadyCreditedIndex(Credited(3110)));

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery("all", 1, 25), CancellationToken.None);

        var row = Assert.Single(result.Value.Items);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.True(row.CanDecide);
        Assert.False(row.CanApprove);
        Assert.False(row.CanAdd);
        Assert.Equal(61455, row.AlreadyCredited?.DocNum);
        Assert.Equal(70310, row.AlreadyCredited?.DocEntry);
        Assert.Equal(new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), row.AlreadyCredited?.EnteredDate);

        // The list names nobody: who keyed it is one more read per person, and the drawer makes it.
        Assert.Null(row.AlreadyCredited?.EnteredByUserCode);
        Assert.Equal(
            "Already credited: credit note 61455 was entered on 15 Aug 2026 for the same customer, reference and total. "
            + "Approving or adding this draft would credit the customer twice. Reject it, or remove the draft in SAP.",
            row.StatusNote);
    }

    /// <summary>
    /// Hidden rows must not leave a page short or a gap behind the cursor. SAP is asked for enough rows
    /// to fill the page once every hidden one below the cursor is dropped, and the next page starts below
    /// the last row shown.
    /// </summary>
    [Fact]
    public async Task Hidden_rows_keep_every_page_full_and_the_cursor_moving()
    {
        var requests = new[] { 5, 4, 3, 2, 1 }.Select(code => Pending(code, 900 + code, stage: 4, originator: 12, template: 7)).ToList();
        var drafts = requests.Select(request => Draft(request.DraftEntry!.Value, "C00477", "Corner Store USD", 10m, null)).ToList();
        var sap = new RecordingSapClient(requests, total: 5, drafts);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1), index: new FakeAlreadyCreditedIndex(Credited(4), Credited(2)));

        var first = (await handler.Handle(new GetCreditNoteApprovalsQuery("pending", 1, 2), CancellationToken.None)).Value;

        Assert.Equal(new[] { 5, 3 }, first.Items.Select(item => item.Code).ToArray());
        Assert.Equal(4, sap.RequestedPageSize);
        Assert.Equal(3, first.NextCursor);
        Assert.Equal(3, first.TotalCount);

        var second = (await handler.Handle(new GetCreditNoteApprovalsQuery("pending", 2, 2, BeforeCode: first.NextCursor), CancellationToken.None)).Value;

        Assert.Equal(new[] { 1 }, second.Items.Select(item => item.Code).ToArray());
        Assert.Equal(3, sap.RequestedPageSize);
        Assert.Null(second.NextCursor);
    }

    /// <summary>A hidden row outside the caller's stages, dates or originator is not theirs to subtract.</summary>
    [Fact]
    public async Task Only_hidden_rows_inside_the_queue_read_are_taken_off_its_count()
    {
        var sap = new RecordingSapClient([Pending(3109, 88122, stage: 4, originator: 12, template: 7)], 1, [Draft(88122, "C00478", "Hillside Superette", 96.25m, null)]);
        var index = new FakeAlreadyCreditedIndex(
            Credited(3200, stage: 9),
            Credited(3201, originator: 31),
            Credited(3202, raised: new DateTime(2026, 7, 1)),
            Credited(3203, status: SapApprovalRequestStatuses.Approved));
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1), FixedStageScope.Stages("Wash Bay Approvals", 4), index);

        var result = await handler.Handle(
            new GetCreditNoteApprovalsQuery("pending", 1, 25, Filter: new CreditNoteApprovalRequestFilter(new DateTime(2026, 8, 1), null, 12)),
            CancellationToken.None);

        Assert.Equal(1, result.Value.TotalCount);
        Assert.Equal(25, sap.RequestedPageSize);
    }

    [Fact]
    public async Task An_index_sap_cannot_build_hides_nothing_and_the_list_still_answers()
    {
        var sap = new RecordingSapClient([Pending(3110, 88123, stage: 4, originator: 12, template: 7)], 1, [Draft(88123, "C00477", "Corner Store USD", 412.65m, null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1), index: new FakeAlreadyCreditedIndex { Fails = true });

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery("pending", 1, 25), CancellationToken.None);

        Assert.False(result.IsError);
        var row = Assert.Single(result.Value.Items);
        Assert.Null(row.AlreadyCredited);
        Assert.Equal(1, result.Value.TotalCount);

        // The approve handler checks afresh and refuses an already-credited draft either way.
        Assert.True(row.CanApprove);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    /// <summary>An index entry: request <paramref name="code"/>, already credited by credit note 61455.</summary>
    private static AlreadyCreditedRequest Credited(
        int code,
        string status = SapApprovalRequestStatuses.Pending,
        int stage = 4,
        int originator = 12,
        DateTime? raised = null) =>
        new(code, status, stage, raised ?? new DateTime(2026, 9, 1), originator, new SAPCreditNote
        {
            DocEntry = 70310,
            DocNum = 61455,
            CardCode = "C00477",
            NumAtCard = "800123",
            DocTotal = 412.65m,
            CreationDate = "2026-08-15T00:00:00Z",
            UserSign = 20,
            Cancelled = SapYesNo.No,
            CancelStatus = "csNo"
        });

    private static GetCreditNoteApprovalsHandler Handler(
        RecordingSapClient sap,
        FakeLookups lookups,
        ICreditNoteApprovalStageScope? scope = null,
        FakeAlreadyCreditedIndex? index = null) =>
        new(
            sap.AsClient(),
            lookups,
            scope ?? FixedStageScope.EveryStage,
            index ?? FakeAlreadyCreditedIndex.Empty,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCreditNoteApprovalsHandler>.Instance);

    private static SAPApprovalRequest Pending(int code, int draftEntry, int stage, int originator, int template) => new()
    {
        Code = code,
        ObjectType = SapObjectTypes.CreditNote,
        Status = SapApprovalRequestStatuses.Pending,
        DraftEntry = draftEntry,
        CurrentStage = stage,
        OriginatorID = originator,
        ApprovalTemplatesID = template,
        CreationDate = "2026-09-01T00:00:00Z",
        CreationTime = "09:41:00"
    };

    private static SAPApprovalRequest Approved(int code, int draftEntry) => new()
    {
        Code = code,
        ObjectType = SapObjectTypes.CreditNote,
        Status = SapApprovalRequestStatuses.Approved,
        DraftEntry = draftEntry,
        CurrentStage = 4,
        OriginatorID = 12,
        ApprovalTemplatesID = 7
    };

    private static SAPCreditNote Draft(
        int docEntry,
        string cardCode,
        string cardName,
        decimal total,
        int? attachmentEntry,
        string authorization = SapDocumentAuthorizationStatuses.Pending,
        string documentStatus = SapDocumentStatuses.Open) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry,
        DocDate = "2026-09-01T00:00:00Z",
        CardCode = cardCode,
        CardName = cardName,
        DocTotal = total,
        DocCurrency = "USD",
        DocumentStatus = documentStatus,
        Cancelled = SapYesNo.No,
        AttachmentEntry = attachmentEntry,
        AuthorizationStatus = authorization,
        DocObjectCode = SapDocObjectCodes.CreditNotes
    };

    /// <summary>A stage with the given approvers, the three users, and template 7 "Returns".</summary>
    private static FakeLookups LookupsWithStage(int code, string name, params int[] approvers)
    {
        var lookups = new FakeLookups();
        lookups.Stages[code] = new SAPApprovalStage
        {
            Code = code,
            Name = name,
            NoOfApproversRequired = 1,
            ApprovalStageApprovers = approvers.Select(id => new SAPApprovalStageApprover { UserID = id }).ToList()
        };
        lookups.Templates[7] = new SAPApprovalTemplate { Code = 7, Name = "Returns", IsActive = SapYesNo.Yes };
        foreach (var user in new[] { Manager, Clerk, Finance })
        {
            lookups.Users[user.InternalKey] = user;
        }

        return lookups;
    }

    private sealed class FakeLookups : ISapApprovalLookups
    {
        public Dictionary<int, SAPUser> Users { get; } = [];
        public Dictionary<int, SAPApprovalStage> Stages { get; } = [];
        public Dictionary<int, SAPApprovalTemplate> Templates { get; } = [];
        public bool UsersUnavailable { get; set; }

        public string ServiceApproverUserCode => "manager";

        public Task<SAPUser?> GetServiceApproverAsync(CancellationToken cancellationToken)
            => UsersUnavailable
                ? throw new HttpRequestException("SAP is not answering")
                : Task.FromResult(Users.Values.FirstOrDefault(user => user.UserCode == "manager"));

        public Task<SAPUser?> GetUserAsync(int internalKey, CancellationToken cancellationToken)
            => UsersUnavailable
                ? throw new HttpRequestException("SAP is not answering")
                : Task.FromResult(Users.GetValueOrDefault(internalKey));

        public Task<SAPApprovalTemplate?> GetTemplateAsync(int code, CancellationToken cancellationToken)
            => Task.FromResult(Templates.GetValueOrDefault(code));

        public Task<SAPApprovalStage?> GetStageAsync(int code, CancellationToken cancellationToken)
            => Task.FromResult(Stages.GetValueOrDefault(code));

        public Task<IReadOnlyList<SAPApprovalStage>> GetStagesByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SAPApprovalStage>>(Stages.Values
                .Where(stage => names.Contains(stage.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .ToList());
    }

    [Fact]
    public async Task A_full_page_hands_back_its_last_code_as_the_cursor_for_the_next_one()
    {
        var codes = new[] { 85053, 85051, 85050 };
        var requests = codes.Select(code => Pending(code, draftEntry: code - 31140, stage: 4, originator: 12, template: 7)).ToList();
        var drafts = requests.Select(request => Draft(request.DraftEntry!.Value, "TMP092", "Pick n Pay Westgate", 150.15m, attachmentEntry: null)).ToList();
        var sap = new RecordingSapClient(requests, total: 9729, drafts);
        var handler = Handler(sap, LookupsWithStage(4, "Wash Bay Approvals", 1, 9));

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery("all", 1, 3), CancellationToken.None);

        Assert.False(result.IsError);

        // The rows come back Code desc, so the last one is the lowest and the next page starts below it.
        Assert.Equal(85050, result.Value.NextCursor);
        Assert.Null(sap.RequestedBeforeCode);
    }

    [Fact]
    public async Task A_short_page_is_the_end_of_the_queue_and_offers_no_cursor()
    {
        var request = Pending(code: 3110, draftEntry: 88123, stage: 4, originator: 12, template: 7);
        var sap = new RecordingSapClient([request], total: 1, [Draft(88123, "SPA059", "Spar Avondale", 10m, attachmentEntry: null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1, 9));

        // One row against a page size of 25: SAP has nothing below it to continue from.
        var result = await handler.Handle(new GetCreditNoteApprovalsQuery("all", 1, 25), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Null(result.Value.NextCursor);
    }

    [Fact]
    public async Task The_cursor_the_page_was_given_is_the_one_sap_is_asked_for()
    {
        var request = Pending(code: 84119, draftEntry: 88123, stage: 4, originator: 12, template: 7);
        var sap = new RecordingSapClient([request], total: 9729, [Draft(88123, "SPA059", "Spar Avondale", 10m, attachmentEntry: null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1, 9));

        await handler.Handle(new GetCreditNoteApprovalsQuery("all", 2, 25, BeforeCode: 84120), CancellationToken.None);

        Assert.Equal(84120, sap.RequestedBeforeCode);
    }

    [Fact]
    public async Task The_raised_dates_and_originator_go_to_sap_with_the_page()
    {
        var request = Pending(code: 84119, draftEntry: 88123, stage: 4, originator: 12, template: 7);
        var sap = new RecordingSapClient([request], total: 1, [Draft(88123, "SPA059", "Spar Avondale", 10m, attachmentEntry: null)]);
        var handler = Handler(sap, LookupsWithStage(4, "Finance review", 1, 9));
        var filter = new CreditNoteApprovalRequestFilter(new DateTime(2026, 11, 1), new DateTime(2026, 11, 9), 12);

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery("pending", 1, 25, Filter: filter), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(filter, sap.RequestedFilter);
    }

    [Fact]
    public void Raised_to_before_raised_from_is_refused()
    {
        var validator = new GetCreditNoteApprovalsValidator();

        var backwards = validator.Validate(new GetCreditNoteApprovalsQuery(
            "pending", 1, 25, Filter: new CreditNoteApprovalRequestFilter(new DateTime(2026, 11, 9), new DateTime(2026, 11, 1))));
        var sameDay = validator.Validate(new GetCreditNoteApprovalsQuery(
            "pending", 1, 25, Filter: new CreditNoteApprovalRequestFilter(new DateTime(2026, 11, 9), new DateTime(2026, 11, 9))));
        var badOriginator = validator.Validate(new GetCreditNoteApprovalsQuery(
            "pending", 1, 25, Filter: new CreditNoteApprovalRequestFilter(OriginatorId: 0)));

        Assert.False(backwards.IsValid);
        Assert.True(sameDay.IsValid);
        Assert.False(badOriginator.IsValid);
    }

    /// <summary>
    /// SAP's queue as the client reads it: Code desc, below the cursor, as many rows as asked for. The list
    /// never reads credit notes itself — the already-credited index does — so that read is not expected.
    /// </summary>
    private sealed class RecordingSapClient(List<SAPApprovalRequest> requests, int total, List<SAPCreditNote> drafts)
    {
        public IReadOnlyCollection<string>? RequestedStatuses { get; private set; }
        public IReadOnlyCollection<int>? RequestedDraftEntries { get; private set; }
        public int? RequestedPageSize { get; private set; }
        public int? RequestedBeforeCode { get; private set; }
        public IReadOnlyCollection<int>? RequestedStageCodes { get; private set; }
        public CreditNoteApprovalRequestFilter? RequestedFilter { get; private set; }

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetCreditNoteApprovalRequestsAsync) => Record(args!),
            nameof(ISAPServiceLayerClient.GetCreditNoteDraftsAsync) => Drafts(args!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private Task<(List<SAPApprovalRequest> Items, int TotalCount)> Record(object?[] args)
        {
            RequestedStatuses = (IReadOnlyCollection<string>)args[0]!;
            RequestedPageSize = (int)args[2]!;
            RequestedBeforeCode = (int?)args[3];
            RequestedStageCodes = (IReadOnlyCollection<int>?)args[4];
            RequestedFilter = (CreditNoteApprovalRequestFilter?)args[5];

            // In the order the test gave them, which is SAP's Code desc wherever a test pages.
            var page = requests
                .Where(request => RequestedBeforeCode is not int cursor || request.Code < cursor)
                .Take(RequestedPageSize.Value)
                .ToList();
            return Task.FromResult((page, total));
        }

        private Task<List<SAPCreditNote>> Drafts(object?[] args)
        {
            var keys = (IReadOnlyCollection<int>)args[0]!;
            RequestedDraftEntries = keys;
            return Task.FromResult(drafts.Where(draft => keys.Contains(draft.DocEntry)).ToList());
        }
    }
}
