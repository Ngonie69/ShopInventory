using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Commands.CreateCreditNote;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Credit note creation, and what happens to a post whose reply never comes back.
/// </summary>
/// <remarks>
/// A credit note used to reach SAP carrying nothing that identified it — no reference, no key, only
/// free-text comments — so a post whose reply was lost could be found by nobody, and the retry that
/// followed raised a second one. A duplicate here is a second ZIMRA credit receipt against one
/// return, which cannot be withdrawn.
///
/// The claim made it worse rather than better: it was released on every failure, including the
/// timeouts and dropped connections where SAP may well have taken the document. And the local row
/// was written last, after fiscalisation, on the caller's own cancellation token — so a closed tab
/// between the post and the save left a credit note in SAP, possibly already lodged with ZIMRA, and
/// nothing at all on this side.
/// </remarks>
public sealed class CreditNoteCreateIdempotencyTests : IDisposable
{
    private const string IdempotencyKey = "4f7b2c1a9e8d0f3b6a5c4d2e1f0a9b8c";
    private const string DerivedReference = "CN-" + IdempotencyKey;
    private static readonly Guid Cashier = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    /// The invoice the from-invoice route credits against, as SAP returns it: four units on line 0,
    /// none credited yet. A test that needs part of it credited already says so with
    /// <see cref="CreditedAlready"/>.
    /// </summary>
    private readonly Invoice _invoice = new()
    {
        DocEntry = 2148037,
        DocNum = 90210,
        CardCode = "ABS006",
        CardName = "Absolute Traders",
        DocTotal = 240.00m,
        DocCurrency = "USD",
        DocumentLines =
        [
            new InvoiceLine
            {
                LineNum = 0,
                ItemCode = "NRI049",
                Quantity = 4m,
                UnitPrice = 60.00m,
                RemainingOpenQuantity = 4m,
                LineStatus = "bost_Open"
            }
        ]
    };

    private static readonly SAPCreditNote Posted = new()
    {
        DocEntry = 41207,
        DocNum = 3318,
        CardCode = "ABS006",
        CardName = "Absolute Traders",
        NumAtCard = DerivedReference,
        DocTotal = 120.00m,
        DocCurrency = "USD"
    };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly IIdempotencyRequestStore _store;
    private readonly CancellationTokenSource _caller = new();

    /// <summary>What the reference lookup finds. Nothing, until a post has been made.</summary>
    private SAPCreditNote? _sapHolds;

    private Exception? _postFailure;
    private bool _replyIsLost;
    private bool _postIsVisibleImmediately = true;
    private bool _callerHangsUpBeforeThePost;
    private Exception? _lookupFailsWith;

    /// <summary>Set to make SAP's read of the invoice being credited fail.</summary>
    private Exception? _invoiceReadFailsWith;

    private readonly List<string> _lookups = [];
    private int _postCount;
    private CreateCreditNoteRequest? _lastPosted;

    public CreditNoteCreateIdempotencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        // The credit note row carries a CreatedByUserId foreign key, so the user has to exist.
        _context.Users.Add(new User
        {
            Id = Cashier,
            Username = "cashier",
            PasswordHash = "not-a-real-hash",
            Role = "Cashier"
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        _store = new IdempotencyRequestStore(
            new SingleContextScopeFactory(_options),
            Options.Create(new SecuritySettings()));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The reference everything else here depends on. Without it SAP holds a credit note that
    /// nothing on this side can ask about.
    /// </summary>
    [Fact]
    public async Task A_credit_note_carries_a_reference_derived_from_its_idempotency_key()
    {
        var result = await CreateAsync();

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(DerivedReference, _lastPosted?.SapReference);
        Assert.Contains(DerivedReference, _lookups);
    }

    /// <summary>
    /// The pre-post guard. An earlier attempt's credit note is adopted rather than raised again.
    /// </summary>
    [Fact]
    public async Task A_credit_note_SAP_already_holds_is_adopted_rather_than_posted_again()
    {
        _sapHolds = Posted;

        var result = await CreateAsync();

        Assert.False(result.IsError);
        Assert.Equal(Posted.DocEntry, result.Value.SAPDocEntry);
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// Adopting has to notice when an earlier attempt already recorded the document, or one SAP
    /// credit note ends up with two local numbers and every local total counts it twice.
    /// </summary>
    [Fact]
    public async Task An_adopted_credit_note_already_recorded_is_not_written_twice()
    {
        var first = await CreateAsync();
        Assert.False(first.IsError);

        // The same request again: SAP holds it, and so does the local database.
        var again = await CreateAsync(newKey: false, freshClaim: true);

        Assert.False(again.IsError);
        Assert.Equal(first.Value.Id, again.Value.Id);
        Assert.Equal(1, await CreditNoteCountAsync());
    }

    /// <summary>
    /// Where a post carries a reference, a lost reply is answerable rather than merely reportable.
    /// </summary>
    [Fact]
    public async Task A_lost_reply_is_recovered_inside_the_same_request()
    {
        _replyIsLost = true;

        var result = await CreateAsync();

        Assert.False(result.IsError);
        Assert.Equal(Posted.DocEntry, result.Value.SAPDocEntry);
        Assert.Equal(1, _postCount);

        // And the credit note is recorded here too, which is the whole reason the save moved ahead
        // of fiscalisation.
        Assert.Equal(1, await CreditNoteCountAsync());
    }

    /// <summary>
    /// The same lost reply with SAP not yet showing the document. The claim has to stand, or the
    /// retry raises a second credit note.
    /// </summary>
    [Fact]
    public async Task A_lost_reply_SAP_cannot_yet_show_keeps_its_claim()
    {
        _replyIsLost = true;
        _postIsVisibleImmediately = false;

        var result = await CreateAsync();

        Assert.True(result.IsError);
        Assert.Single(await ClaimsAsync());
    }

    /// <summary>
    /// A refusal proves nothing was created, so the claim goes back and the caller can correct the
    /// document and send it again under the same key.
    /// </summary>
    [Fact]
    public async Task A_refusal_from_SAP_gives_the_claim_back()
    {
        _postFailure = new SapRequestRejectedException(
            "create the credit note",
            System.Net.HttpStatusCode.BadRequest,
            "Item NRI049 is not managed in warehouse KEFSHOP");

        var refused = await CreateAsync();

        Assert.True(refused.IsError);
        Assert.Equal("CreditNote.SapRejected", refused.FirstError.Code);
        Assert.Empty(await ClaimsAsync());
    }

    /// <summary>
    /// A gateway failure is not a refusal. Something in front of SAP answered, and the credit note
    /// may exist behind it, so the claim stands.
    /// </summary>
    [Fact]
    public async Task A_failure_that_is_not_SAPs_own_answer_keeps_its_claim()
    {
        _postFailure = new HttpRequestException("502 Bad Gateway");
        _postIsVisibleImmediately = false;

        var result = await CreateAsync();

        Assert.True(result.IsError);
        Assert.Single(await ClaimsAsync());
    }

    /// <summary>
    /// The disconnect that used to leave a credit note in SAP and nothing on this side.
    /// </summary>
    [Fact]
    public async Task A_caller_who_hangs_up_does_not_abort_the_post_or_the_save()
    {
        _callerHangsUpBeforeThePost = true;

        var result = await CreateAsync();

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(1, _postCount);
        Assert.Equal(1, await CreditNoteCountAsync());
    }

    /// <summary>
    /// "I could not ask" must never read as "it is not there" — that is how a return gets credited
    /// twice.
    /// </summary>
    [Fact]
    public async Task An_unanswerable_lookup_never_clears_the_post()
    {
        _lookupFailsWith = new HttpRequestException("Connection refused");

        var result = await CreateAsync();

        Assert.True(result.IsError);
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// The control for the refusals below: a line SAP still holds open is credited, and the credit
    /// note is based on that line.
    /// </summary>
    [Fact]
    public async Task A_line_SAP_still_holds_open_is_credited()
    {
        var result = await CreateFromInvoiceAsync();

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(1, _postCount);
        Assert.Equal<int?>(_invoice.DocEntry, _lastPosted?.OriginalInvoiceDocEntry);
        Assert.Equal<int?>(0, Assert.Single(_lastPosted!.Lines).OriginalInvoiceLineId);
    }

    /// <summary>
    /// Invoice 772109 on 2026-09-11. Credit memo 55294 had credited every line in full, and SAP read
    /// each one RemainingOpenQuantity 0, bost_Close. The guard used to total the invoice's credit notes
    /// from a lookup that was a stub answering "none", so it would have cleared a second full credit.
    /// </summary>
    [Fact]
    public async Task An_invoice_SAP_reports_fully_credited_is_not_credited_again()
    {
        CreditedAlready(stillOpen: 0m, lineStatus: "bost_Close");

        var result = await CreateFromInvoiceAsync();

        Assert.True(result.IsError);
        Assert.Contains("already been fully credited", result.FirstError.Description);
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// A partial credit leaves the rest creditable and no more than the rest. On invoice 770686 a line
    /// of 90 read 20 open once 70 had been credited.
    /// </summary>
    [Fact]
    public async Task A_credit_cannot_take_more_than_a_partly_credited_line_still_holds()
    {
        CreditedAlready(stillOpen: 1m);

        var result = await CreateFromInvoiceAsync(quantity: 2m);

        Assert.True(result.IsError);
        Assert.Contains("exceeds the 1 still creditable", result.FirstError.Description);
        Assert.Equal(0, _postCount);
    }

    /// <summary>The boundary: exactly what is still open may be credited.</summary>
    [Fact]
    public async Task What_a_partly_credited_line_still_holds_can_be_credited()
    {
        CreditedAlready(stillOpen: 2m);

        var result = await CreateFromInvoiceAsync(quantity: 2m);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(1, _postCount);
    }

    /// <summary>
    /// Two credit lines against one invoice line draw on one balance. Checked apart, each would pass.
    /// </summary>
    [Fact]
    public async Task Credit_lines_against_the_same_invoice_line_are_counted_together()
    {
        CreditedAlready(stillOpen: 3m);

        var result = await CreateFromInvoiceAsync(quantity: 2m, lineCount: 2);

        Assert.True(result.IsError);
        Assert.Contains("exceeds the 3 still creditable", result.FirstError.Description);
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// SAP could not be reached for the invoice at all. Nothing is known about what it has been
    /// credited, so nothing more is.
    /// </summary>
    [Fact]
    public async Task An_invoice_SAP_cannot_be_asked_for_is_not_credited_further()
    {
        _invoiceReadFailsWith = new HttpRequestException("Connection refused");

        var result = await CreateFromInvoiceAsync();

        Assert.True(result.IsError);
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// SAP answered without the open quantities — a narrower $select, a renamed field. A missing number
    /// is not a line with nothing credited, and reading it as one is how a return gets credited twice.
    /// </summary>
    [Fact]
    public async Task An_invoice_read_without_its_open_quantities_is_not_credited_further()
    {
        _invoice.DocumentLines![0].RemainingOpenQuantity = null;

        var result = await CreateFromInvoiceAsync();

        Assert.True(result.IsError);
        Assert.Contains("could not be read", result.FirstError.Description);
        Assert.Equal(0, _postCount);
    }

    /// <summary>A credit line that names no line of the invoice has no balance to be checked against.</summary>
    [Fact]
    public async Task A_credit_line_matching_no_invoice_line_is_refused()
    {
        var result = await CreateFromInvoiceAsync(itemCode: "FET018", lineNum: 7);

        Assert.True(result.IsError);
        Assert.Contains("has no line for item FET018", result.FirstError.Description);
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// The guard reads two fields off SAP's JSON, and a misspelt name deserialises to null — which the
    /// guard refuses, so every credit note from an invoice would be refused. These lines are SAP's own
    /// answer on 2026-09-11: invoice 772109 line 0, fully credited, and invoice 770686 line 23, 70 of
    /// 90 credited.
    /// </summary>
    [Fact]
    public void SAPs_invoice_lines_carry_their_open_quantity_and_status()
    {
        const string sapJson = """
            {
              "DocEntry": 2342939,
              "DocNum": 772109,
              "DocumentLines": [
                { "LineNum": 0, "ItemCode": "ICS025", "Quantity": 17.0, "RemainingOpenQuantity": 0.0, "LineStatus": "bost_Close", "OpenAmount": 106.25 },
                { "LineNum": 23, "ItemCode": "ICV008", "Quantity": 90.0, "RemainingOpenQuantity": 20.0, "LineStatus": "bost_Open", "OpenAmount": 801.9 }
              ]
            }
            """;

        var invoice = System.Text.Json.JsonSerializer.Deserialize<Invoice>(sapJson)!;

        Assert.Equal(0m, invoice.DocumentLines![0].RemainingOpenQuantity);
        Assert.Equal("bost_Close", invoice.DocumentLines[0].LineStatus);
        Assert.Equal(20m, invoice.DocumentLines[1].RemainingOpenQuantity);
        Assert.Equal("bost_Open", invoice.DocumentLines[1].LineStatus);
    }

    /// <summary>What credit memos already based on invoice line 0 have left open on it.</summary>
    private void CreditedAlready(decimal stillOpen, string lineStatus = "bost_Open")
    {
        var line = _invoice.DocumentLines![0];
        line.RemainingOpenQuantity = stillOpen;
        line.LineStatus = lineStatus;
    }

    private async Task<ErrorOr.ErrorOr<CreditNoteDto>> CreateFromInvoiceAsync(
        decimal quantity = 2m,
        string itemCode = "NRI049",
        int lineNum = 0,
        int lineCount = 1)
    {
        var lines = Enumerable.Range(0, lineCount)
            .Select(_ => new CreateCreditNoteLineRequest
            {
                ItemCode = itemCode,
                Quantity = quantity,
                UnitPrice = 60.00m,
                OriginalInvoiceLineId = lineNum
            })
            .ToList();

        try
        {
            var creditNote = await BuildService().CreateFromInvoiceAsync(
                _invoice.DocEntry, lines, "Damaged in transit", Cashier, IdempotencyKey, _caller.Token);

            return creditNote;
        }
        catch (InvalidOperationException refusal)
        {
            // The service's own refusal, which the handler turns into CreditNote.InvalidOperation.
            return ErrorOr.Error.Validation("CreditNote.InvalidOperation", refusal.Message);
        }
    }

    private async Task<ErrorOr.ErrorOr<CreditNoteDto>> CreateAsync(
        bool newKey = false,
        bool freshClaim = false)
    {
        if (freshClaim)
        {
            // The claim from the previous call completed. A genuinely new request under a new key is
            // what reaches the SAP guard, so clear the store rather than replaying.
            using var context = new ApplicationDbContext(_options);
            await context.IdempotencyRequests.ExecuteDeleteAsync();
        }

        var request = new CreateCreditNoteRequest
        {
            CardCode = "ABS006",
            CardName = "Absolute Traders",
            Reason = "Damaged in transit",
            Currency = "USD",
            RestockItems = false,
            ClientRequestId = newKey ? Guid.NewGuid().ToString("N") : IdempotencyKey,
            Lines =
            [
                new CreateCreditNoteLineRequest
                {
                    ItemCode = "NRI049",
                    ItemDescription = "Feta 1kg",
                    Quantity = 2m,
                    UnitPrice = 60.00m,
                    WarehouseCode = "KEFSHOP"
                }
            ]
        };

        return await CreateHandler().Handle(
            new CreateCreditNoteCommand(request, Cashier), _caller.Token);
    }

    private async Task<int> CreditNoteCountAsync()
    {
        using var context = new ApplicationDbContext(_options);
        return await context.CreditNotes.CountAsync();
    }

    private async Task<List<IdempotencyRequestEntity>> ClaimsAsync()
    {
        using var context = new ApplicationDbContext(_options);
        return await context.IdempotencyRequests.AsNoTracking().ToListAsync();
    }

    private CreateCreditNoteHandler CreateHandler() =>
        new(
            BuildService(),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            StubProxy.Unused<MediatR.ISender>(),
            StubProxy.Unused<IFiscalReceiptReader>(),
            _store,
            StubProxy.For<INotificationService>((_, _) => Task.FromResult(new NotificationDto())),
            Options.Create(new SecuritySettings()),
            NullLogger<CreateCreditNoteHandler>.Instance);

    private ICreditNoteService BuildService() =>
        new CreditNoteService(
            new ApplicationDbContext(_options),
            BuildSapClient(),
            BuildFiscalizationService(),
            StubProxy.For<ICreditNoteProjectionSyncService>((_, _) => Task.CompletedTask),
            StubProxy.Unused<IStockLedger>(),
            NullLogger<CreditNoteService>.Instance);

    private ISAPServiceLayerClient BuildSapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetCreditNoteByReferenceAsync) => (object)ReferenceLookup(args),
            nameof(ISAPServiceLayerClient.CreateCreditNoteAsync) => Post(args),
            nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync) => ReadInvoice(),
            // Not GetCreditNotesByInvoiceAsync. It is a stub that always answers "none", so nothing on
            // this path may depend on it, and a call to it fails the test.
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private Task<SAPCreditNote?> ReferenceLookup(object?[]? args)
    {
        _lookups.Add((string)args![0]!);

        if (_lookupFailsWith is not null)
        {
            throw _lookupFailsWith;
        }

        return Task.FromResult(_sapHolds);
    }

    private Task<Invoice?> ReadInvoice()
    {
        if (_invoiceReadFailsWith is not null)
        {
            throw _invoiceReadFailsWith;
        }

        return Task.FromResult<Invoice?>(_invoice);
    }

    private Task<SAPCreditNote> Post(object?[]? args)
    {
        // Deliberate: a stub that ignores its token makes the hang-up test pass against the bug it
        // was written for.
        ((CancellationToken)args![1]!).ThrowIfCancellationRequested();

        _lastPosted = (CreateCreditNoteRequest)args[0]!;
        _postCount++;

        // The caller gives up while the post is in flight. Everything after this — the save above
        // all — has to carry on regardless, because SAP now holds a document this side would
        // otherwise have no record of.
        HangUpIfAsked();

        if (_replyIsLost)
        {
            if (_postIsVisibleImmediately)
            {
                _sapHolds = Posted;
            }

            throw new TaskCanceledException("The request was canceled", new TimeoutException());
        }

        if (_postFailure is not null)
        {
            throw _postFailure;
        }

        _sapHolds = Posted;
        return Task.FromResult(Posted);
    }

    private IFiscalizationService BuildFiscalizationService() =>
        StubProxy.For<IFiscalizationService>((method, _) =>
            method.Name == nameof(IFiscalizationService.FiscalizeCreditNoteAsync)
                ? Task.FromResult(new FiscalizationResult { Success = true })
                : throw new InvalidOperationException(
                    $"IFiscalizationService.{method.Name} was not expected on this path."));

    private void HangUpIfAsked()
    {
        if (_callerHangsUpBeforeThePost)
        {
            _caller.Cancel();
        }
    }

    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;

        public void Dispose()
        {
        }
    }
}
