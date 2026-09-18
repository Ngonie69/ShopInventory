using System.Net;
using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.CreateTransferRequest;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The purchase documents and transfer requests under an idempotency key (<see cref="IdempotentCreate"/>).
/// </summary>
/// <remarks>
/// None of these reaches SAP carrying anything of ours, so a post whose reply was lost cannot be
/// looked up and adopted. What stands between a lost reply and a second purchase invoice is the
/// claim: completed, it replays the document; given back, it lets a corrected document through; kept,
/// it refuses the retry. Which of the three happens is the whole behaviour, and it is driven here
/// through the real purchase invoice handler and the real store, with only SAP stubbed.
/// </remarks>
public sealed class PurchaseDocumentIdempotencyTests : IDisposable
{
    private const string Key = "7c1e0a9b3d4f4e2a8b6c5d4e3f2a1b0c";
    private static readonly Guid Requester = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly IdempotencyRequestStore _store;

    /// <summary>What the stubbed SAP does on each post, in order; the last repeats.</summary>
    private readonly Queue<Func<SAPPurchaseInvoice>> _sapAnswers = new();
    private int _posts;

    public PurchaseDocumentIdempotencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
        _store = new IdempotencyRequestStore(new SingleDbContextScopeFactory(_options), Options.Create(new SecuritySettings()));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_retry_after_a_created_invoice_gets_that_invoice_and_posts_nothing()
    {
        SapCreates(docEntry: 5501, docNum: 9001);

        var first = await Handle(Invoice(Key));
        var retry = await Handle(Invoice(Key));

        Assert.False(first.IsError);
        Assert.False(retry.IsError);
        Assert.Equal(9001, retry.Value.DocNum);
        Assert.Equal(1, _posts);
    }

    [Fact]
    public async Task A_refused_invoice_gives_its_key_back_so_the_corrected_one_goes_through()
    {
        _sapAnswers.Enqueue(() => throw new SapRequestRejectedException(
            "create the purchase invoice", HttpStatusCode.BadRequest, "Enter a valid tax code"));
        SapCreates(docEntry: 5502, docNum: 9002);

        var refused = await Handle(Invoice(Key));
        var corrected = await Handle(Invoice(Key, taxCode: "O8"));

        Assert.True(refused.IsError);
        // SAP's own words reach the operator, not a wrapper around them.
        Assert.Equal("Enter a valid tax code", refused.FirstError.Description);
        Assert.False(corrected.IsError, corrected.IsError ? corrected.FirstError.Description : "");
        Assert.Equal(9002, corrected.Value.DocNum);
        Assert.Equal(2, _posts);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("lost-reply")]
    [InlineData("unreadable-reply")]
    public async Task A_post_whose_answer_never_came_keeps_its_key_and_refuses_the_retry(string failure)
    {
        _sapAnswers.Enqueue(failure switch
        {
            // HttpClient's own timeout, which is what a slow SAP looks like past the commit point.
            "timeout" => () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            "lost-reply" => () => throw new HttpRequestException("The response ended prematurely", null, HttpStatusCode.BadGateway),
            // Thrown after SAP committed: the bare Exception CreatePurchaseInvoiceAsync raises when
            // it cannot read the created document back.
            _ => () => throw new Exception("Failed to deserialize created purchase invoice")
        });
        SapCreates(docEntry: 5503, docNum: 9003);

        var first = await Handle(Invoice(Key));
        var retry = await Handle(Invoice(Key));

        Assert.True(first.IsError);
        Assert.Equal("Idempotency.OutcomeUnknown", first.FirstError.Code);
        Assert.True(retry.IsError);
        Assert.Equal("Idempotency.PostOutcomeUnconfirmed", retry.FirstError.Code);
        Assert.Equal(1, _posts);
    }

    [Fact]
    public async Task A_failure_before_anything_was_sent_gives_the_key_back()
    {
        // A login failure, marked by the client as raised before the document went out.
        _sapAnswers.Enqueue(() =>
        {
            var loginFailure = new HttpRequestException("No connection could be made to SAP");
            SapFailureClassifier.MarkNotSent(loginFailure);
            throw loginFailure;
        });
        SapCreates(docEntry: 5504, docNum: 9004);

        var failed = await Handle(Invoice(Key));
        var retry = await Handle(Invoice(Key));

        Assert.True(failed.IsError);
        Assert.NotEqual("Idempotency.OutcomeUnknown", failed.FirstError.Code);
        Assert.False(retry.IsError, retry.IsError ? retry.FirstError.Description : "");
        Assert.Equal(9004, retry.Value.DocNum);
    }

    [Fact]
    public async Task A_key_reused_for_a_different_invoice_is_refused()
    {
        SapCreates(docEntry: 5505, docNum: 9005);

        await Handle(Invoice(Key));
        var other = await Handle(Invoice(Key, supplier: "SUP-OTHER"));

        Assert.True(other.IsError);
        Assert.Equal("Idempotency.RequestMismatch", other.FirstError.Code);
        Assert.Equal(1, _posts);
    }

    [Fact]
    public async Task Without_a_key_every_request_posts_as_it_always_did()
    {
        SapCreates(docEntry: 5506, docNum: 9006);

        await Handle(Invoice(key: null));
        await Handle(Invoice(key: null));

        Assert.Equal(2, _posts);
        Assert.Empty(_context.Set<IdempotencyRequestEntity>());
    }

    [Fact]
    public async Task The_post_runs_on_a_token_the_caller_cannot_cancel()
    {
        CancellationToken seen = default;
        using var caller = new CancellationTokenSource();
        var handler = new CreatePurchaseInvoiceHandler(
            StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            {
                seen = (CancellationToken)args![1]!;
                caller.Cancel();
                return Task.FromResult(new SAPPurchaseInvoice { DocEntry = 5507, DocNum = 9007, CardCode = "SUP001" });
            }),
            _store,
            Notifications(),
            NullLogger<CreatePurchaseInvoiceHandler>.Instance);

        var result = await handler.Handle(new CreatePurchaseInvoiceCommand(Invoice(Key)), caller.Token);

        Assert.False(result.IsError);
        Assert.False(seen.CanBeCanceled, "A closed tab must not abort a post SAP may already be committing.");
    }

    [Fact]
    public async Task A_transfer_request_whose_reply_was_lost_keeps_its_key()
    {
        _context.Users.Add(new User
        {
            Id = Requester, Username = "rudo", PasswordHash = "x", Role = "Manager", IsActive = true
        });
        await _context.SaveChangesAsync();

        var posts = 0;
        var handler = new CreateTransferRequestHandler(
            _context,
            StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetWarehousesAsync) => Task.FromResult(new List<WarehouseDto>
                {
                    new() { WarehouseCode = "KEFSHOP" },
                    new() { WarehouseCode = "KEFDEPOT" }
                }),
                // Sent, and the connection dropped before the reply. Before this change a transfer
                // request in this state was reported as a plain connection error, which invited
                // the retry that raises a second one.
                nameof(ISAPServiceLayerClient.CreateInventoryTransferRequestAsync) => LostReply(ref posts),
                _ => throw new InvalidOperationException($"{method.Name} was not expected on this path.")
            }),
            _store,
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            StubProxy.Unused<IInventoryTransferApprovalService>(),
            Notifications(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<CreateTransferRequestHandler>.Instance);

        var first = await handler.Handle(TransferRequest(), CancellationToken.None);
        var retry = await handler.Handle(TransferRequest(), CancellationToken.None);

        Assert.Equal("InventoryTransfer.SapPostUncertain", first.FirstError.Code);
        Assert.Equal("Idempotency.PostOutcomeUnconfirmed", retry.FirstError.Code);
        Assert.Equal(1, posts);
    }

    private static Task<InventoryTransferRequest> LostReply(ref int posts)
    {
        posts++;
        return Task.FromException<InventoryTransferRequest>(
            new HttpRequestException("The response ended prematurely", null, HttpStatusCode.BadGateway));
    }

    private CreateTransferRequestCommand TransferRequest() =>
        new(
            new CreateTransferRequestDto
            {
                ClientRequestId = Key,
                FromWarehouse = "KEFSHOP",
                ToWarehouse = "KEFDEPOT",
                Lines =
                [
                    new CreateTransferRequestLineDto
                    {
                        ItemCode = "YOG143", Quantity = 5m, UoMCode = "EA",
                        FromWarehouseCode = "KEFSHOP", ToWarehouseCode = "KEFDEPOT"
                    }
                ]
            },
            Requester);

    private void SapCreates(int docEntry, int docNum) =>
        _sapAnswers.Enqueue(() => new SAPPurchaseInvoice { DocEntry = docEntry, DocNum = docNum, CardCode = "SUP001" });

    private Task<ErrorOr<PurchaseInvoiceDto>> Handle(CreatePurchaseInvoiceRequest request) =>
        new CreatePurchaseInvoiceHandler(
                StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                {
                    if (method.Name != nameof(ISAPServiceLayerClient.CreatePurchaseInvoiceAsync))
                    {
                        throw new InvalidOperationException($"{method.Name} was not expected on this path.");
                    }

                    _posts++;
                    var answer = _sapAnswers.Count > 1 ? _sapAnswers.Dequeue() : _sapAnswers.Peek();
                    try
                    {
                        return Task.FromResult(answer());
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException<SAPPurchaseInvoice>(ex);
                    }
                }),
                _store,
                Notifications(),
                NullLogger<CreatePurchaseInvoiceHandler>.Instance)
            .Handle(new CreatePurchaseInvoiceCommand(request), CancellationToken.None);

    private static INotificationService Notifications() =>
        StubProxy.For<INotificationService>((_, _) => Task.FromResult(new NotificationDto()));

    private static CreatePurchaseInvoiceRequest Invoice(string? key, string supplier = "SUP001", string? taxCode = null) =>
        new()
        {
            ClientRequestId = key,
            CardCode = supplier,
            DocCurrency = "USD",
            Lines =
            [
                new CreatePurchaseInvoiceLineRequest
                {
                    ItemCode = "CHE001", Quantity = 4m, UnitPrice = 12.5m, TaxCode = taxCode
                }
            ]
        };
}
