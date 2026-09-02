using System.Net;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Adding an approved draft: the one SAP write that posts money, then the reads and writes that make
/// the new credit note visible and fiscalised here.
/// </summary>
/// <remarks>
/// The add happens once and stands on its own. Everything after it — projection, fiscalisation, the
/// fiscal transaction row — is best effort, because the credit note exists in SAP the moment the save
/// returns, and a failure downstream must be reported and recorded, never allowed to fail the add or
/// invite a second one.
/// </remarks>
public sealed class CreditNoteApprovalAddTests : IDisposable
{
    private static readonly Guid Ngoni = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public CreditNoteApprovalAddTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Adding_saves_the_draft_reads_the_credit_note_back_projects_and_fiscalises_it()
    {
        var sap = new RecordingSap(Approved(3110), OpenDraft(88123)) { SaveReturns = 9001, DraftAfterSave = ClosedDraft(88123) };
        var fiscal = new RecordingFiscalisation { Result = new FiscalizationResult { Success = true, ReceiptGlobalNo = "4411", Message = "ok" } };
        var audit = new RecordingAuditService();
        var handler = Handler(sap, fiscal, audit);

        var result = await handler.Handle(Command(3110), CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        var save = Assert.Single(sap.Saves);
        Assert.Equal(88123, save.DraftEntry);
        Assert.False(save.Token.CanBeCanceled, "the save must run on a token the caller cannot cancel");

        Assert.Equal(9001, Assert.Single(sap.Projected).DocEntry);

        var fiscalised = Assert.Single(fiscal.Calls);
        Assert.Equal(9001, fiscalised.Document.DocEntry);
        Assert.Equal("77001", fiscalised.OriginalInvoiceNumber);
        Assert.Equal(245.60m, fiscalised.Document.DocTotal);

        var recorded = Assert.Single(fiscal.Recorded);
        Assert.Equal("CreditNote", recorded.Request.DocumentType);
        Assert.Equal("CreditNoteApprovalAdd", recorded.Request.SourceSystem);
        Assert.Equal("Success", recorded.Request.Status);
        Assert.Equal(9001, recorded.Request.DocNum);
        Assert.Equal(4411, recorded.Request.ReceiptGlobalNo);
        Assert.Equal("77001", recorded.Request.OriginalInvoiceNumber);

        Assert.True(result.Value.Resolved);
        Assert.Equal(9001, result.Value.CreditNoteDocEntry);
        Assert.Equal(9001, result.Value.CreditNoteDocNum);
        Assert.True(result.Value.Fiscalisation.Success);
        Assert.Contains("#9001", result.Value.Message);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AddApprovedCreditNote, entry.Action);
        Assert.True(entry.Success);
        Assert.Empty(_context.ExceptionCenterIncidents);
    }

    /// <summary>
    /// SAP answers the conversion with no content and names the created document nowhere: it deletes
    /// the approval request outright and never populates ObjectEntry (both measured on
    /// KEFALOS_TEST_3). The document is therefore identified as one this customer did not have before
    /// the add, carrying the draft's total.
    /// </summary>
    [Fact]
    public async Task When_saps_answer_names_no_document_the_new_credit_note_is_identified_by_what_changed()
    {
        var sap = new RecordingSap(Approved(3110), OpenDraft(88123))
        {
            SaveReturns = null,
            DraftAfterSave = ClosedDraft(88123),
            NewestBefore = new SAPCreditNote { DocEntry = 8000, DocTotal = -100m },
            NewestAfter = new SAPCreditNote { DocEntry = 9002, DocTotal = 245.60m },
            CreditNoteDocEntry = 9002
        };

        var result = await Handler(sap, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3110), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(result.Value.Resolved);
        Assert.Equal(9002, result.Value.CreditNoteDocEntry);
    }

    /// <summary>
    /// The customer's newest credit note is only the created one if it is genuinely new and matches
    /// the draft. A document that was already there, or one whose total differs, must not be claimed:
    /// on KEFALOS_TEST_3 a DocNum match found a different customer's credit note entirely.
    /// </summary>
    [Fact]
    public async Task A_credit_note_that_was_already_there_or_does_not_match_is_not_claimed()
    {
        var unchanged = new RecordingSap(Approved(3110), OpenDraft(88123))
        {
            SaveReturns = null,
            DraftAfterSave = ClosedDraft(88123),
            NewestBefore = new SAPCreditNote { DocEntry = 9002, DocTotal = 245.60m },
            NewestAfter = new SAPCreditNote { DocEntry = 9002, DocTotal = 245.60m }
        };
        var first = await Handler(unchanged, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3110), CancellationToken.None);
        Assert.False(first.IsError);
        Assert.False(first.Value.Resolved);

        var mismatched = new RecordingSap(Approved(3111), OpenDraft(88123))
        {
            SaveReturns = null,
            DraftAfterSave = ClosedDraft(88123),
            NewestBefore = new SAPCreditNote { DocEntry = 8000, DocTotal = -100m },
            NewestAfter = new SAPCreditNote { DocEntry = 9003, DocTotal = 999.99m }
        };
        var second = await Handler(mismatched, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3111), CancellationToken.None);
        Assert.False(second.IsError, "the add still happened; only the identification failed");
        Assert.False(second.Value.Resolved);
        Assert.Contains("did not say which credit note", second.Value.Message);
    }

    [Fact]
    public async Task Only_an_approved_request_can_be_added_and_an_added_one_names_its_credit_note()
    {
        var pending = new RecordingSap(Pending(3110), OpenDraft(88123));
        var pendingResult = await Handler(pending, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3110), CancellationToken.None);
        Assert.Equal("CreditNoteApproval.NotApproved", pendingResult.FirstError.Code);
        Assert.Empty(pending.Saves);

        var generated = new RecordingSap(Generated(3111, objectEntry: 9001), OpenDraft(88123));
        var generatedResult = await Handler(generated, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3111), CancellationToken.None);
        Assert.Equal("CreditNoteApproval.AlreadyAdded", generatedResult.FirstError.Code);
        Assert.Contains("9001", generatedResult.FirstError.Description);
        Assert.Empty(generated.Saves);
    }

    [Fact]
    public async Task A_closed_or_no_longer_approved_draft_is_refused()
    {
        var closed = OpenDraft(88123);
        closed.DocumentStatus = SapDocumentStatuses.Closed;
        var closedResult = await Handler(new RecordingSap(Approved(3110), closed), new RecordingFiscalisation(), new RecordingAuditService())
            .Handle(Command(3110), CancellationToken.None);
        Assert.Equal("CreditNoteApproval.DraftNotOpen", closedResult.FirstError.Code);

        var reset = OpenDraft(88123);
        reset.AuthorizationStatus = SapDocumentAuthorizationStatuses.Pending;
        var resetResult = await Handler(new RecordingSap(Approved(3110), reset), new RecordingFiscalisation(), new RecordingAuditService())
            .Handle(Command(3110), CancellationToken.None);
        Assert.Equal("CreditNoteApproval.NotApproved", resetResult.FirstError.Code);
    }

    [Fact]
    public async Task A_fiscalisation_failure_leaves_an_incident_and_the_add_stands()
    {
        var sap = new RecordingSap(Approved(3110), OpenDraft(88123)) { SaveReturns = 9001 };
        var fiscal = new RecordingFiscalisation { Result = new FiscalizationResult { Success = false, Message = "FDMS down" } };

        var result = await Handler(sap, fiscal, new RecordingAuditService()).Handle(Command(3110), CancellationToken.None);

        Assert.False(result.IsError, "a fiscal failure must not fail the add — the credit note already exists");
        Assert.False(result.Value.Fiscalisation.Success);
        Assert.Contains("FDMS down", result.Value.Message);
        Assert.Equal("Failed", Assert.Single(fiscal.Recorded).Request.Status);

        var incident = Assert.Single(_context.ExceptionCenterIncidents);
        Assert.Equal(CreditNoteFiscalisationIncidents.Source, incident.Source);
        Assert.Equal("FDMS down", incident.LastError);
        Assert.Equal("SAP-CN-9001", incident.Reference);
    }

    [Fact]
    public async Task Fiscalisation_after_add_can_be_switched_off()
    {
        var sap = new RecordingSap(Approved(3110), OpenDraft(88123)) { SaveReturns = 9001 };
        var fiscal = new RecordingFiscalisation();

        var result = await Handler(sap, fiscal, new RecordingAuditService(), fiscaliseAfterAdd: false).Handle(Command(3110), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(fiscal.Calls);
        Assert.False(result.Value.Fiscalisation.Attempted);
        Assert.Single(sap.Projected);
    }

    [Fact]
    public async Task Saps_refusal_is_reported_and_nothing_downstream_runs()
    {
        var sap = new RecordingSap(Approved(3110), OpenDraft(88123)) { RefuseWith = "Document is locked by another user" };
        var fiscal = new RecordingFiscalisation();
        var audit = new RecordingAuditService();

        var result = await Handler(sap, fiscal, audit).Handle(Command(3110), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.SapRejected", result.FirstError.Code);
        Assert.Contains("locked by another user", result.FirstError.Description);
        Assert.Empty(sap.Projected);
        Assert.Empty(fiscal.Calls);
        Assert.False(Assert.Single(audit.Entries).Success);
    }

    [Fact]
    public async Task A_repeat_replays_the_first_answer_without_a_second_save()
    {
        var sap = new RecordingSap(Approved(3110), OpenDraft(88123)) { SaveReturns = 9001, DraftAfterSave = ClosedDraft(88123) };
        var handler = Handler(sap, new RecordingFiscalisation(), new RecordingAuditService(), store: Store());

        var first = await handler.Handle(Command(3110), CancellationToken.None);
        var second = await handler.Handle(Command(3110), CancellationToken.None);

        Assert.False(first.IsError);
        Assert.False(second.IsError, "a retry must replay the credit note the add produced, not be refused as already added");
        Assert.Equal(first.Value.CreditNoteDocEntry, second.Value.CreditNoteDocEntry);
        Assert.Single(sap.Saves);
    }

    /// <summary>
    /// After a call that failed mid-flight, the draft is the only witness: SAP deletes the approval
    /// request the moment the conversion succeeds, so asking it whether the add landed always answers
    /// "gone" and would report every successful add as uncertain.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_is_uncertain_unless_the_draft_reads_back_closed()
    {
        var uncertain = new RecordingSap(Approved(3110), OpenDraft(88123))
        {
            ThrowOnSave = new HttpRequestException("connection reset"),
            DraftAfterSave = OpenDraft(88123)
        };
        var uncertainResult = await Handler(uncertain, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3110), CancellationToken.None);
        Assert.Equal("CreditNoteApproval.AddUncertain", uncertainResult.FirstError.Code);

        var landed = new RecordingSap(Approved(3110), OpenDraft(88123))
        {
            ThrowOnSave = new HttpRequestException("connection reset"),
            DraftAfterSave = ClosedDraft(88123),
            NewestBefore = new SAPCreditNote { DocEntry = 8000, DocTotal = -1m },
            NewestAfter = new SAPCreditNote { DocEntry = 9001, DocTotal = 245.60m },
            CreditNoteDocEntry = 9001
        };
        var landedResult = await Handler(landed, new RecordingFiscalisation(), new RecordingAuditService()).Handle(Command(3110), CancellationToken.None);
        Assert.False(landedResult.IsError, "the draft is closed, so the failed call is not a failed add");
        Assert.Equal(9001, landedResult.Value.CreditNoteDocEntry);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private AddApprovedCreditNoteHandler Handler(
        RecordingSap sap,
        RecordingFiscalisation fiscal,
        RecordingAuditService audit,
        bool fiscaliseAfterAdd = true,
        IIdempotencyRequestStore? store = null) => new(
        _context,
        sap.AsClient(),
        sap.AsProjection(),
        fiscal.AsService(),
        fiscal.AsSender(),
        store ?? Store(),
        audit,
        Options.Create(new SAPSettings { Enabled = true }),
        Options.Create(new CreditNoteApprovalSettings { FiscaliseAfterAdd = fiscaliseAfterAdd }),
        NullLogger<AddApprovedCreditNoteHandler>.Instance);

    private IIdempotencyRequestStore Store()
        => new IdempotencyRequestStore(new SingleDbContextScopeFactory(_options), Options.Create(new SecuritySettings()));

    private static AddApprovedCreditNoteCommand Command(int code) => new(code, Ngoni, "ngoni", null);

    private static SAPApprovalRequest Pending(int code) => WithStatus(code, SapApprovalRequestStatuses.Pending);

    private static SAPApprovalRequest Approved(int code) => WithStatus(code, SapApprovalRequestStatuses.Approved);

    private static SAPApprovalRequest Generated(int code, int objectEntry)
    {
        var request = WithStatus(code, SapApprovalRequestStatuses.Generated);
        request.ObjectEntry = objectEntry;
        return request;
    }

    private static SAPApprovalRequest WithStatus(int code, string status) => new()
    {
        Code = code, ObjectType = SapObjectTypes.CreditNote, Status = status, DraftEntry = 88123, CurrentStage = 4
    };

    private static SAPCreditNote ClosedDraft(int docEntry)
    {
        var draft = OpenDraft(docEntry);
        draft.DocumentStatus = SapDocumentStatuses.Closed;
        draft.AuthorizationStatus = SapDocumentAuthorizationStatuses.Without;
        return draft;
    }

    private static SAPCreditNote OpenDraft(int docEntry) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry,
        CardCode = "SPA059",
        CardName = "Spar Avondale",
        DocTotal = 245.60m,
        VatSum = 32.03m,
        DocCurrency = "USD",
        DocumentStatus = SapDocumentStatuses.Open,
        Cancelled = SapYesNo.No,
        AuthorizationStatus = SapDocumentAuthorizationStatuses.Approved,
        DocObjectCode = SapDocObjectCodes.CreditNotes
    };

    private static SAPCreditNote CreditNote(int docEntry) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry,
        CardCode = "SPA059",
        CardName = "Spar Avondale",
        DocTotal = -245.60m,
        VatSum = -32.03m,
        DocCurrency = "USD",
        Comments = "Damaged goods",
        DocumentStatus = SapDocumentStatuses.Open,
        DocumentLines =
        [
            new SAPCreditNoteLine { LineNum = 0, ItemCode = "CHE011", Quantity = -4, UnitPrice = 61.40m, LineTotal = -245.60m, BaseType = 13, BaseEntry = 77001 }
        ]
    };

    private sealed class RecordingSap(SAPApprovalRequest current, SAPCreditNote draft)
    {
        private bool _saved;

        public int? SaveReturns { get; init; }
        public int? CreditNoteDocEntry { get; init; }
        public string? RefuseWith { get; init; }
        public Exception? ThrowOnSave { get; init; }

        /// <summary>The draft as SAP reports it once the add has landed — closed, in the real thing.</summary>
        public SAPCreditNote? DraftAfterSave { get; init; }

        /// <summary>The customer's newest credit note, before and after the add.</summary>
        public SAPCreditNote? NewestBefore { get; init; }
        public SAPCreditNote? NewestAfter { get; init; }

        public List<(int DraftEntry, CancellationToken Token)> Saves { get; } = [];
        public List<SAPCreditNote> Projected { get; } = [];

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetApprovalRequestAsync)
                => Task.FromResult<SAPApprovalRequest?>(current),
            nameof(ISAPServiceLayerClient.GetCreditNoteDraftAsync)
                => Task.FromResult(_saved ? DraftAfterSave ?? draft : draft),
            nameof(ISAPServiceLayerClient.GetNewestCreditNoteForCustomerAsync)
                => Task.FromResult(_saved ? NewestAfter : NewestBefore),
            nameof(ISAPServiceLayerClient.SaveDraftToDocumentAsync) => Save(args!),
            nameof(ISAPServiceLayerClient.GetCreditNoteByDocEntryAsync)
                => Task.FromResult<SAPCreditNote?>((int)args![0]! == (CreditNoteDocEntry ?? SaveReturns) ? CreditNote((int)args[0]!) : null),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        public ICreditNoteProjectionSyncService AsProjection() => StubProxy.For<ICreditNoteProjectionSyncService>((method, args) =>
        {
            if (method.Name != nameof(ICreditNoteProjectionSyncService.UpsertAsync))
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Projected.AddRange((IReadOnlyCollection<SAPCreditNote>)args![0]!);
            return Task.CompletedTask;
        });

        private Task<int?> Save(object?[] args)
        {
            _saved = true;
            if (RefuseWith is not null)
            {
                throw new SapRequestRejectedException("save draft", HttpStatusCode.BadRequest, RefuseWith);
            }

            if (ThrowOnSave is not null)
            {
                throw ThrowOnSave;
            }

            Saves.Add(((int)args[0]!, (CancellationToken)args[1]!));
            return Task.FromResult(SaveReturns);
        }
    }

    private sealed class RecordingFiscalisation
    {
        public FiscalizationResult Result { get; init; } = new() { Success = true, Message = "ok" };
        public List<(InvoiceDto Document, string OriginalInvoiceNumber)> Calls { get; } = [];
        public List<SyncFiscalTransactionCommand> Recorded { get; } = [];

        public IFiscalizationService AsService() => StubProxy.For<IFiscalizationService>((method, args) =>
        {
            if (method.Name != nameof(IFiscalizationService.FiscalizeCreditNoteAsync))
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Calls.Add(((InvoiceDto)args![0]!, (string)args[1]!));
            return Task.FromResult(Result);
        });

        public ISender AsSender() => StubProxy.For<ISender>((method, args) =>
        {
            if (method.Name != nameof(ISender.Send) || args?[0] is not SyncFiscalTransactionCommand command)
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Recorded.Add(command);
            return Task.FromResult<ErrorOr<FiscalTransactionLogItemDto>>(new FiscalTransactionLogItemDto());
        });
    }
}
