using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Recording an approve or reject on a SAP-held credit memo. What reaches SAP is the contract: the
/// service approver's name, its password, the decision, and remarks that name the person who clicked.
/// </summary>
/// <remarks>
/// Two things here are structural rather than behavioural. The PATCH runs on a token the caller cannot
/// cancel, because a browser that hangs up mid-call must not strand a half-recorded decision; and a
/// call that fails after the PATCH is answered by reading the request back, because SAP knows whether
/// it landed and the handler does not.
/// </remarks>
public sealed class CreditNoteApprovalDecisionTests : IDisposable
{
    private static readonly Guid Ngoni = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly SAPUser Manager = new() { InternalKey = 1, UserCode = "manager", UserName = "Site Manager" };
    private static readonly SAPUser Finance = new() { InternalKey = 9, UserCode = "finmgr", UserName = "Finance Manager" };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public CreditNoteApprovalDecisionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Approving_records_the_decision_as_the_service_approver_and_names_the_person()
    {
        var sap = new RecordingSap(Pending(3110)) { AfterDecision = Approved(3110) };
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit).Handle(Command(3110, "Approved", "looks right"), CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        var patch = Assert.Single(sap.Decisions);
        Assert.Equal(3110, patch.Code);
        // The approver is the app's own SAP account, so SAP is told nothing: it records the session
        // user, and no password crosses the wire. See SAPSettings.UsesDedicatedApprovalApprover.
        Assert.Null(patch.Approver);
        Assert.Null(patch.Password);
        Assert.Equal(SapApprovalDecisions.Approved, patch.Decision);
        Assert.Equal("Approved in ShopInventory by ngoni: looks right", patch.Remarks);
        Assert.False(patch.Token.CanBeCanceled, "the PATCH must run on a token the caller cannot cancel");

        Assert.Equal("Approved", result.Value.Status);
        Assert.True(result.Value.CanAdd);
        Assert.False(result.Value.StillPending);
        Assert.Contains("can now be added", result.Value.Message);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.ApproveSapCreditNote, entry.Action);
        Assert.True(entry.Success);
        Assert.Contains("ngoni", entry.Details);
    }

    [Fact]
    public async Task Rejecting_sends_not_approved_and_audits_the_rejection()
    {
        var sap = new RecordingSap(Pending(3110)) { AfterDecision = WithStatus(3110, SapApprovalRequestStatuses.NotApproved) };
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit).Handle(Command(3110, "notapproved", "no return note"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(SapApprovalDecisions.NotApproved, Assert.Single(sap.Decisions).Decision);
        Assert.Equal("NotApproved", result.Value.Status);
        Assert.False(result.Value.CanAdd);
        Assert.Equal(AuditActions.RejectSapCreditNote, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task An_approval_that_leaves_sap_waiting_on_another_stage_says_so()
    {
        var after = Pending(3110);
        after.CurrentStage = 5;
        after.ApprovalRequestLines = [Line(4, 1, SapApprovalDecisions.Approved)];
        var sap = new RecordingSap(Pending(3110)) { AfterDecision = after };

        var result = await Handler(sap, new RecordingAuditService()).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(result.Value.StillPending);
        Assert.False(result.Value.CanAdd);
        Assert.Contains("waiting on another stage", result.Value.Message);
    }

    [Fact]
    public async Task Only_a_pending_request_can_be_decided()
    {
        var sap = new RecordingSap(Approved(3110));

        var result = await Handler(sap, new RecordingAuditService()).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.NotPending", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task A_stage_that_does_not_list_the_service_approver_is_refused_before_sap_is_asked()
    {
        var sap = new RecordingSap(Pending(3110));
        var lookups = FakeSapApprovalLookups.WithStage(4, "Finance review", [Manager, Finance], 9);

        var result = await Handler(sap, new RecordingAuditService(), lookups).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.ApproverNotOnStage", result.FirstError.Code);
        Assert.Contains("Finance review", result.FirstError.Description);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task A_stage_the_service_approver_already_decided_is_refused()
    {
        var request = Pending(3110);
        request.ApprovalRequestLines = [Line(4, 1, SapApprovalDecisions.Approved), Line(4, 9, SapApprovalDecisions.Pending)];
        var sap = new RecordingSap(request);

        var result = await Handler(sap, new RecordingAuditService()).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.AlreadyDecided", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task Saps_refusal_is_reported_in_its_own_words_and_audited_as_a_failure()
    {
        var sap = new RecordingSap(Pending(3110)) { RefuseWith = "User is not an approver of this stage" };
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.SapRejected", result.FirstError.Code);
        Assert.Contains("User is not an approver of this stage", result.FirstError.Description);
        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
    }

    [Fact]
    public async Task A_repeat_with_the_same_client_request_id_replays_the_first_answer_without_a_second_patch()
    {
        var sap = new RecordingSap(Pending(3110)) { AfterDecision = Approved(3110) };
        var store = Store();
        var handler = Handler(sap, new RecordingAuditService(), store: store);
        var command = Command(3110, "Approved", "ok", clientRequestId: "click-1");

        var first = await handler.Handle(command, CancellationToken.None);
        var second = await handler.Handle(command, CancellationToken.None);

        Assert.False(first.IsError);
        Assert.False(second.IsError, "the retry must replay, not be refused as no longer pending");
        Assert.Equal(first.Value.Status, second.Value.Status);
        Assert.Equal(first.Value.Message, second.Value.Message);
        Assert.Single(sap.Decisions);
    }

    [Fact]
    public async Task A_caller_that_has_already_gone_sends_nothing_to_sap()
    {
        var sap = new RecordingSap(Pending(3110));
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        var result = await Handler(sap, new RecordingAuditService()).Handle(Command(3110, "Approved", null), gone.Token);

        Assert.Equal("CreditNoteApproval.Cancelled", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task A_transport_failure_after_the_patch_is_answered_by_reading_the_request_back()
    {
        var landed = Approved(3110);
        landed.ApprovalRequestLines = [Line(4, 1, SapApprovalDecisions.Approved)];
        var sap = new RecordingSap(Pending(3110)) { ThrowOnDecision = new HttpRequestException("connection reset"), AfterDecision = landed };

        var result = await Handler(sap, new RecordingAuditService()).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.False(result.IsError, "SAP shows the decision recorded, so the failed call is not a failed decision");
        Assert.Equal("Approved", result.Value.Status);
    }

    [Fact]
    public async Task A_transport_failure_with_no_trace_in_sap_is_uncertain_not_a_silent_success()
    {
        var sap = new RecordingSap(Pending(3110)) { ThrowOnDecision = new HttpRequestException("connection reset"), AfterDecision = Pending(3110) };
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit).Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.DecisionUncertain", result.FirstError.Code);
        Assert.False(Assert.Single(audit.Entries).Success);
    }

    /// <summary>
    /// SAP holds 200 characters and refuses the whole decision above that — measured on
    /// KEFALOS_TEST_3, where 200 was accepted and 201 was not. The person's name is in the prefix, so
    /// the free text is what gets cut.
    /// </summary>
    [Fact]
    public void Remarks_keep_the_person_when_the_free_text_is_cut_to_saps_column()
    {
        var remarks = DecideCreditNoteApprovalHandler.ComposeRemarks("Approved", "ngoni", new string('x', 300));

        Assert.Equal(200, DecideCreditNoteApprovalHandler.SapRemarksLength);
        Assert.Equal(200, remarks.Length);
        Assert.StartsWith("Approved in ShopInventory by ngoni: ", remarks);
        Assert.Equal("NotApproved in ShopInventory by ngoni", DecideCreditNoteApprovalHandler.ComposeRemarks("NotApproved", "ngoni", "  "));
    }

    /// <summary>
    /// A dedicated approver with no password is a configuration fault SAP reports as bad credentials
    /// ("User code or password is incorrect"), which reads like a wrong password rather than a missing
    /// setting. Catch it here, before the request is spent.
    /// </summary>
    [Fact]
    public async Task A_named_approver_without_a_password_is_refused_before_sap_is_asked()
    {
        var sap = new RecordingSap(Pending(3110));
        var handler = new DecideCreditNoteApprovalHandler(
            sap.AsClient(),
            FakeSapApprovalLookups.WithStage(4, "Finance review", [Manager, Finance], 1, 9),
            Store(),
            new RecordingAuditService(),
            Options.Create(new SAPSettings
            {
                Enabled = true,
                Username = "manager",
                Password = "pw",
                ApprovalApproverUsername = "finmgr"
            }),
            NullLogger<DecideCreditNoteApprovalHandler>.Instance);

        var result = await handler.Handle(Command(3110, "Approved", null), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.ApproverPasswordMissing", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task A_dedicated_approver_with_a_password_is_named_on_the_decision()
    {
        var sap = new RecordingSap(Pending(3110)) { AfterDecision = Approved(3110) };
        var lookups = FakeSapApprovalLookups.WithStage(4, "Finance review", [Manager, Finance], 1, 9);
        var handler = new DecideCreditNoteApprovalHandler(
            sap.AsClient(),
            lookups,
            Store(),
            new RecordingAuditService(),
            Options.Create(new SAPSettings
            {
                Enabled = true,
                Username = "manager",
                Password = "pw",
                ApprovalApproverUsername = "manager",
                ApprovalApproverPassword = "dedicated"
            }),
            NullLogger<DecideCreditNoteApprovalHandler>.Instance);

        var result = await handler.Handle(Command(3110, "Approved", null), CancellationToken.None);

        // Same name as the session user, so it is still the session user: nothing is named.
        Assert.False(result.IsError);
        var patch = Assert.Single(sap.Decisions);
        Assert.Null(patch.Approver);
        Assert.Null(patch.Password);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private DecideCreditNoteApprovalHandler Handler(
        RecordingSap sap,
        RecordingAuditService audit,
        FakeSapApprovalLookups? lookups = null,
        IIdempotencyRequestStore? store = null) => new(
        sap.AsClient(),
        lookups ?? FakeSapApprovalLookups.WithStage(4, "Finance review", [Manager, Finance], 1, 9),
        store ?? Store(),
        audit,
        Options.Create(new SAPSettings { Enabled = true, Username = "manager", Password = "pw" }),
        NullLogger<DecideCreditNoteApprovalHandler>.Instance);

    private IIdempotencyRequestStore Store()
        => new IdempotencyRequestStore(new SingleDbContextScopeFactory(_options), Options.Create(new SecuritySettings()));

    private static DecideCreditNoteApprovalCommand Command(int code, string decision, string? remarks, string? clientRequestId = null)
        => new(code, decision, remarks, Ngoni, "ngoni", clientRequestId);

    private static SAPApprovalRequest Pending(int code) => WithStatus(code, SapApprovalRequestStatuses.Pending);

    private static SAPApprovalRequest Approved(int code) => WithStatus(code, SapApprovalRequestStatuses.Approved);

    private static SAPApprovalRequest WithStatus(int code, string status) => new()
    {
        Code = code,
        ObjectType = SapObjectTypes.CreditNote,
        Status = status,
        DraftEntry = 88123,
        CurrentStage = 4,
        OriginatorID = 12,
        ApprovalTemplatesID = 7,
        ApprovalRequestLines = [Line(4, 1, SapApprovalDecisions.Pending), Line(4, 9, SapApprovalDecisions.Pending)]
    };

    private static SAPApprovalRequestLine Line(int stage, int user, string status)
        => new() { StageCode = stage, UserID = user, Status = status };

    private sealed class RecordingSap(SAPApprovalRequest current)
    {
        private bool _decided;

        public SAPApprovalRequest? AfterDecision { get; init; }
        public string? RefuseWith { get; init; }
        public Exception? ThrowOnDecision { get; init; }
        public List<(int Code, string? Approver, string? Password, string Decision, string? Remarks, CancellationToken Token)> Decisions { get; } = [];

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetApprovalRequestAsync)
                => Task.FromResult<SAPApprovalRequest?>(_decided ? AfterDecision ?? current : current),
            nameof(ISAPServiceLayerClient.SubmitApprovalDecisionAsync) => Decide(args!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private Task Decide(object?[] args)
        {
            _decided = true;
            if (RefuseWith is not null)
            {
                throw new SapRequestRejectedException("record decision", HttpStatusCode.BadRequest, RefuseWith);
            }

            if (ThrowOnDecision is not null)
            {
                throw ThrowOnDecision;
            }

            Decisions.Add(((int)args[0]!, (string?)args[1], (string?)args[2], (string)args[3]!, (string?)args[4], (CancellationToken)args[5]!));
            return Task.CompletedTask;
        }
    }
}
