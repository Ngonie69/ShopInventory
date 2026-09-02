using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            Options.Create(new SAPSettings { Enabled = false }),
            NullLogger<GetCreditNoteApprovalsHandler>.Instance);

        var result = await handler.Handle(new GetCreditNoteApprovalsQuery(null, 1, 25), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("CreditNoteApproval.SapDisabled", result.FirstError.Code);
        Assert.Null(sap.RequestedStatuses);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static GetCreditNoteApprovalsHandler Handler(RecordingSapClient sap, FakeLookups lookups) =>
        new(sap.AsClient(), lookups, Options.Create(new SAPSettings { Enabled = true }), NullLogger<GetCreditNoteApprovalsHandler>.Instance);

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
    }

    private sealed class RecordingSapClient(List<SAPApprovalRequest> requests, int total, List<SAPCreditNote> drafts)
    {
        public IReadOnlyCollection<string>? RequestedStatuses { get; private set; }
        public IReadOnlyCollection<int>? RequestedDraftEntries { get; private set; }

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetCreditNoteApprovalRequestsAsync) => Record(args!),
            nameof(ISAPServiceLayerClient.GetCreditNoteDraftsAsync) => Drafts(args!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private Task<(List<SAPApprovalRequest> Items, int TotalCount)> Record(object?[] args)
        {
            RequestedStatuses = (IReadOnlyCollection<string>)args[0]!;
            return Task.FromResult((requests, total));
        }

        private Task<List<SAPCreditNote>> Drafts(object?[] args)
        {
            var keys = (IReadOnlyCollection<int>)args[0]!;
            RequestedDraftEntries = keys;
            return Task.FromResult(drafts.Where(draft => keys.Contains(draft.DocEntry)).ToList());
        }
    }
}
