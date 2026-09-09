using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Commands.CreateInvoice;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The interactive invoice route, and what happens to a post whose reply never comes back.
/// </summary>
/// <remarks>
/// Every other producer of an invoice — till sales, van sales, end-of-day consolidation, stock
/// reservations — writes a business key into SAP's <c>U_Van_saleorder</c> and asks about it before
/// posting, and keeps its local marker when a failure leaves the outcome unknown.
/// <c>POST /api/Invoice</c> did neither. Its idempotency claim was released on every failure,
/// including a timeout over an invoice SAP had already committed, and <c>ClientRequestId</c> is
/// never sent to the Service Layer — so the retry re-acquired a clean claim, had nothing to ask SAP
/// about, and posted a second invoice, with a second ZIMRA receipt, against one sale.
///
/// These tests are about closing that: the derived key that gives this route something to ask
/// about, and the claim that survives a failure it cannot account for.
/// </remarks>
public sealed class InvoiceCreateIdempotencyTests : IDisposable
{
    private const string IdempotencyKey = "8d1f4c2ba9034e6f9b7a5c3e1d0f8a26";
    private const string DerivedReference = "WEB-" + IdempotencyKey;

    private static readonly Invoice Posted = new()
    {
        DocEntry = 88120,
        DocNum = 6104,
        CardCode = "ABS006",
        U_Van_saleorder = DerivedReference
    };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly IIdempotencyRequestStore _store;

    /// <summary>What the key lookup finds. Nothing, until a post has been made.</summary>
    private Invoice? _sapHolds;

    /// <summary>Raised by the post instead of returning an invoice.</summary>
    private Exception? _postFailure;

    /// <summary>Set when the post commits in SAP and its reply never reaches us.</summary>
    private bool _replyIsLost;

    /// <summary>Set to make the key lookup fail, as an unreachable Service Layer would.</summary>
    private Exception? _lookupFailsWith;

    private readonly List<string> _lookups = [];
    private int _postCount;
    private CreateInvoiceRequest? _lastPosted;

    public InvoiceCreateIdempotencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

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
    /// The reference every guard downstream depends on. Without it the invoice reaches SAP carrying
    /// nothing that can be searched for afterwards.
    /// </summary>
    [Fact]
    public async Task An_invoice_with_no_business_key_carries_one_derived_from_its_idempotency_key()
    {
        var result = await CreateInvoiceAsync();

        Assert.False(result.IsError);
        Assert.Equal(DerivedReference, _lastPosted?.U_Van_saleorder);
        Assert.Contains(DerivedReference, _lookups);
    }

    [Fact]
    public async Task A_caller_that_names_its_own_business_key_keeps_it()
    {
        var result = await CreateInvoiceAsync(saleReference: "POS-3312");

        Assert.False(result.IsError);
        Assert.Equal("POS-3312", _lastPosted?.U_Van_saleorder);
    }

    /// <summary>
    /// The behaviour the whole change exists for. The first attempt commits in SAP and loses its
    /// reply; the retry has to be handed that invoice rather than post a second one.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_lost_reply_is_given_the_invoice_SAP_holds()
    {
        _replyIsLost = true;
        var first = await CreateInvoiceAsync();

        Assert.True(first.IsError);
        Assert.Equal("Invoice.SapTimeout", first.FirstError.Code);

        _replyIsLost = false;
        var retry = await CreateInvoiceAsync();

        Assert.False(retry.IsError);
        Assert.Equal(Posted.DocEntry, retry.Value.Invoice?.DocEntry);
        Assert.Equal(1, _postCount);
    }

    /// <summary>
    /// The same lost reply, with SAP not yet showing the invoice it committed. The retry waits
    /// rather than posts: this is the window the observed duplicates all fell inside.
    /// </summary>
    [Fact]
    public async Task A_retry_inside_the_grace_window_is_refused_while_SAP_cannot_answer()
    {
        _replyIsLost = true;
        await CreateInvoiceAsync();

        // SAP committed it and has not made it visible yet.
        _sapHolds = null;
        _replyIsLost = false;

        var retry = await CreateInvoiceAsync();

        Assert.True(retry.IsError);
        Assert.Equal("Idempotency.PostOutcomeUnknown", retry.FirstError.Code);
        Assert.Contains(DerivedReference, retry.FirstError.Description);
        Assert.Equal(1, _postCount);
    }

    /// <summary>
    /// Past the window with SAP still showing nothing, the post genuinely never landed. Holding the
    /// claim forever would be its own failure: the sale would never be invoiced at all.
    /// </summary>
    [Fact]
    public async Task Past_the_grace_window_a_post_SAP_never_took_is_made_good()
    {
        _replyIsLost = true;
        await CreateInvoiceAsync();

        _sapHolds = null;
        _replyIsLost = false;
        await AgeTheClaimAsync(TimeSpan.FromMinutes(20));

        var retry = await CreateInvoiceAsync();

        Assert.False(retry.IsError);
        Assert.Equal(2, _postCount);
    }

    /// <summary>
    /// A refusal proves the invoice does not exist, so the claim goes back — otherwise a caller who
    /// fixes the document is locked out of its own key for the whole expiry window.
    /// </summary>
    [Fact]
    public async Task A_refusal_from_SAP_gives_the_claim_back()
    {
        _postFailure = new SapRequestRejectedException(
            "create the invoice",
            System.Net.HttpStatusCode.BadRequest,
            "Item NRI049 is not managed in warehouse KEFSHOP");

        var refused = await CreateInvoiceAsync();
        Assert.True(refused.IsError);
        Assert.Empty(await ClaimsAsync());

        _postFailure = null;
        var retry = await CreateInvoiceAsync();

        Assert.False(retry.IsError);
        Assert.Equal(2, _postCount);
    }

    /// <summary>
    /// A failure before the post reaches SAP proves nothing was created just as well, whatever its
    /// type. Keeping the claim for those would make an unreachable Service Layer look like a
    /// committed invoice, and lock the caller out until the window ran down.
    /// </summary>
    [Fact]
    public async Task A_failure_before_the_post_gives_the_claim_back()
    {
        _lookupFailsWith = new HttpRequestException("No such host is known");

        var unreachable = await CreateInvoiceAsync();

        Assert.True(unreachable.IsError);
        Assert.Empty(await ClaimsAsync());
        Assert.Equal(0, _postCount);
    }

    /// <summary>
    /// A retry while the outcome is unknown must never be answered from a lookup that could not be
    /// made. Treating "I could not ask" as "it is not there" is how an invoice gets posted twice.
    /// </summary>
    [Fact]
    public async Task An_unanswerable_lookup_never_clears_a_retry_to_post()
    {
        _replyIsLost = true;
        await CreateInvoiceAsync();

        _replyIsLost = false;
        _lookupFailsWith = new HttpRequestException("Connection refused");

        var retry = await CreateInvoiceAsync();

        Assert.True(retry.IsError);
        Assert.Equal(1, _postCount);

        // And the claim still stands, so the attempt after it asks SAP too.
        Assert.Single(await ClaimsAsync());
    }

    [Fact]
    public async Task A_caller_may_not_hand_write_a_derived_reference()
    {
        var result = await CreateInvoiceAsync(saleReference: DerivedReference);

        Assert.True(result.IsError);
        Assert.Equal("Invoice.ReservedSaleReference", result.FirstError.Code);
        Assert.Equal(0, _postCount);
    }

    private async Task<ErrorOr.ErrorOr<InvoiceCreatedResponseDto>> CreateInvoiceAsync(
        string? saleReference = null)
    {
        var request = new CreateInvoiceRequest
        {
            CardCode = "ABS006",
            DocCurrency = "USD",
            ClientRequestId = IdempotencyKey,
            U_Van_saleorder = saleReference,
            Lines =
            [
                new CreateInvoiceLineRequest
                {
                    ItemCode = "NRI049",
                    Quantity = 4m,
                    WarehouseCode = "KEFSHOP"
                }
            ]
        };

        return await CreateHandler().Handle(
            new CreateInvoiceCommand(request, true, BatchAllocationStrategy.FEFO, null, null),
            CancellationToken.None);
    }

    /// <summary>
    /// Moves the standing claim back in time. That is the only thing separating "the post may still
    /// be in flight" from "the post never landed".
    /// </summary>
    private async Task AgeTheClaimAsync(TimeSpan by)
    {
        using var context = new ApplicationDbContext(_options);
        foreach (var claim in await context.IdempotencyRequests.ToListAsync())
        {
            claim.CreatedAtUtc -= by;
        }

        await context.SaveChangesAsync();
    }

    private async Task<List<IdempotencyRequestEntity>> ClaimsAsync()
    {
        using var context = new ApplicationDbContext(_options);
        return await context.IdempotencyRequests.AsNoTracking().ToListAsync();
    }

    private CreateInvoiceHandler CreateHandler() =>
        new(
            BuildSapClient(),
            BuildPriceCatalog(),
            BuildBatchValidation(),
            StubProxy.For<IInventoryLockService>((_, _) => Task.CompletedTask),
            BuildStockLedger(),
            StubProxy.For<IInvoiceFiscalizationQueue>((_, _) => true),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            _store,
            StubProxy.Unused<INotificationService>(),
            new ApplicationDbContext(_options),
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(new SecuritySettings()),
            NullLogger<CreateInvoiceHandler>.Instance);

    private ISAPServiceLayerClient BuildSapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            // Cast so the two arms are not narrowed to a common Task<Invoice?>: the lookup answers
            // with a nullable invoice, the post never does.
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => (object)KeyLookup(args),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Post(args),
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private Task<Invoice?> KeyLookup(object?[]? args)
    {
        _lookups.Add((string)args![0]!);

        if (_lookupFailsWith is not null)
        {
            throw _lookupFailsWith;
        }

        return Task.FromResult(_sapHolds);
    }

    private Task<Invoice> Post(object?[]? args)
    {
        _lastPosted = (CreateInvoiceRequest)args![0]!;
        _postCount++;

        if (_replyIsLost)
        {
            // What a committed post whose reply is lost looks like from here: SAP holds the invoice,
            // and this side is told only that the request timed out.
            _sapHolds = Posted;
            throw new TaskCanceledException("The request was canceled", new TimeoutException());
        }

        if (_postFailure is not null)
        {
            throw _postFailure;
        }

        _sapHolds = Posted;
        return Task.FromResult(Posted);
    }

    private ILocalPriceCatalogService BuildPriceCatalog() =>
        StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(new LocalBusinessPartnerPricingResult
                {
                    BusinessPartner = new BusinessPartnerDto { CardCode = "ABS006" },
                    Prices = new ItemPricesByListResponseDto
                    {
                        Prices = [new ItemPriceByListDto { ItemCode = "NRI049", Price = 60.00m }]
                    }
                })
                : throw new InvalidOperationException(
                    $"ILocalPriceCatalogService.{method.Name} was not expected on this path."));

    private IBatchInventoryValidationService BuildBatchValidation() =>
        StubProxy.For<IBatchInventoryValidationService>((method, _) => method.Name switch
        {
            nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync) =>
                Task.FromResult(new BatchAllocationResult()),
            nameof(IBatchInventoryValidationService.PrePostValidationAsync) =>
                Task.FromResult(new BatchStockValidationResponseDto { IsValid = true }),
            _ => throw new InvalidOperationException(
                $"IBatchInventoryValidationService.{method.Name} was not expected on this path.")
        });

    private IStockLedger BuildStockLedger() =>
        StubProxy.For<IStockLedger>((method, _) => method.Name switch
        {
            nameof(IStockLedger.TryCommitAsync) => Task.FromResult(StockLedgerOutcome.Success),
            nameof(IStockLedger.ReleaseAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException(
                $"IStockLedger.{method.Name} was not expected on this path.")
        });

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
