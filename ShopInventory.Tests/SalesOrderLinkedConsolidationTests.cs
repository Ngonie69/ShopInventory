using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van converts a sales order into an invoice; the invoice is queued and posted by end-of-day
/// consolidation. These pin that the posted invoice is based on the order's SAP document, and that the
/// link never costs the customer their invoice.
/// </summary>
public sealed class SalesOrderLinkedConsolidationTests : IDisposable
{
    private const string CardCode = "VAN014";
    private const int OrderDocEntry = 40211;
    private static readonly DateTime SaleDate = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<InvoiceQueueEntity> _queueEntries = [];

    /// <summary>The base-document fields of every line, as each post was sent.</summary>
    private readonly List<List<(string? ItemCode, decimal Quantity, int? BaseType, int? BaseEntry, int? BaseLine)>> _posts = [];

    private readonly List<int> _orderReads = [];

    private SAPSalesOrder? _sapOrder = new()
    {
        DocEntry = OrderDocEntry,
        DocNum = 88120,
        CardCode = CardCode,
        DocumentStatus = "bost_Open",
        Cancelled = "tNO",
        DocumentLines =
        [
            new SAPSalesOrderLine { LineNum = 0, ItemCode = "NRI049", RemainingOpenQuantity = 4m, LineStatus = "bost_Open" },
            new SAPSalesOrderLine { LineNum = 1, ItemCode = "CHE011", RemainingOpenQuantity = 2m, LineStatus = "bost_Open" }
        ]
    };

    /// <summary>What the first post throws, when set. Later posts succeed.</summary>
    private Exception? _firstPostFailure;

    public SalesOrderLinkedConsolidationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_converted_order_is_invoiced_against_its_SAP_document()
    {
        var orderId = await GivenAPostedSalesOrderAsync();
        GivenAQueuedConversion(orderId);

        var result = await Consolidate();

        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Equal([OrderDocEntry], _orderReads);

        var post = Assert.Single(_posts);
        Assert.Collection(
            post,
            line => Assert.Equal(("NRI049", 4m, (int?)17, (int?)OrderDocEntry, (int?)0), line),
            line => Assert.Equal(("CHE011", 2m, (int?)17, (int?)OrderDocEntry, (int?)1), line));
    }

    /// <summary>
    /// The fallback. SAP answered the linked invoice with a refusal, so nothing was created, and the
    /// day is posted as it would have been before orders were linked rather than failing.
    /// </summary>
    [Fact]
    public async Task A_refused_linked_invoice_is_posted_again_without_the_link()
    {
        var orderId = await GivenAPostedSalesOrderAsync();
        GivenAQueuedConversion(orderId);
        _firstPostFailure = new SapRequestRejectedException(
            "create invoice",
            HttpStatusCode.BadRequest,
            "Target document line quantity exceeds base document open quantity");

        var result = await Consolidate();

        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Equal(2, _posts.Count);
        Assert.All(_posts[0], line => Assert.Equal(OrderDocEntry, line.BaseEntry));
        Assert.All(_posts[1], line =>
        {
            Assert.Null(line.BaseType);
            Assert.Null(line.BaseEntry);
            Assert.Null(line.BaseLine);
        });

        Assert.Equal(ConsolidationStatus.Posted, (await _context.SaleConsolidations.SingleAsync()).Status);
    }

    /// <summary>
    /// A failure that may have committed is never answered with a second post: that would be two
    /// invoices for one day's sales. It goes to the existing lost-reply recovery, which asks SAP.
    /// </summary>
    [Fact]
    public async Task An_uncertain_failure_is_not_reposted_without_the_link()
    {
        var orderId = await GivenAPostedSalesOrderAsync();
        GivenAQueuedConversion(orderId);
        _firstPostFailure = new TimeoutException("The Service Layer did not answer.");

        var result = await Consolidate();

        Assert.Single(_posts);
        Assert.Equal(1, result.FailedPostings);
    }

    [Fact]
    public async Task An_order_that_never_reached_SAP_leaves_the_invoice_unlinked()
    {
        var orderId = await GivenAPostedSalesOrderAsync(sapDocEntry: null);
        GivenAQueuedConversion(orderId);

        var result = await Consolidate();

        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Empty(_orderReads);
        Assert.All(Assert.Single(_posts), line => Assert.Null(line.BaseEntry));
    }

    [Theory]
    [InlineData("another-partner")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    [InlineData("missing")]
    public async Task An_order_SAP_cannot_invoice_against_leaves_the_invoice_unlinked(string state)
    {
        var orderId = await GivenAPostedSalesOrderAsync();
        GivenAQueuedConversion(orderId);

        switch (state)
        {
            case "another-partner": _sapOrder!.CardCode = "VAN015"; break;
            case "closed": _sapOrder!.DocumentStatus = "bost_Close"; break;
            case "cancelled": _sapOrder!.Cancelled = "tYES"; break;
            case "missing": _sapOrder = null; break;
        }

        var result = await Consolidate();

        Assert.Equal(1, result.SuccessfulPostings);
        Assert.All(Assert.Single(_posts), line => Assert.Null(line.BaseEntry));
    }

    /// <summary>A queued sale that was not converted from an order asks SAP about no order at all.</summary>
    [Fact]
    public async Task A_queued_sale_with_no_order_reads_no_order()
    {
        GivenAQueuedConversion(salesOrderId: null);

        var result = await Consolidate();

        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Empty(_orderReads);
        Assert.All(Assert.Single(_posts), line => Assert.Null(line.BaseEntry));
    }

    private async Task<ConsolidateDailySalesResult> Consolidate()
    {
        var result = await CreateHandler().Handle(
            new ConsolidateDailySalesCommand(SaleDate), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        return result.Value;
    }

    private async Task<int> GivenAPostedSalesOrderAsync(int? sapDocEntry = OrderDocEntry)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = "SO-20260916-0042",
            OrderDate = SaleDate,
            CardCode = CardCode,
            Currency = "USD",
            Status = SalesOrderStatus.Fulfilled,
            Source = SalesOrderSource.Mobile,
            SAPDocEntry = sapDocEntry,
            SAPDocNum = sapDocEntry.HasValue ? 88120 : null
        };

        _context.SalesOrders.Add(order);
        await _context.SaveChangesAsync();
        return order.Id;
    }

    private void GivenAQueuedConversion(int? salesOrderId) =>
        _queueEntries.Add(new InvoiceQueueEntity
        {
            Id = 501,
            ExternalReference = "VAN-CONV-7781",
            SourceSystem = "KefalosVanSales",
            CustomerCode = CardCode,
            TotalAmount = 264.00m,
            Currency = "USD",
            WarehouseCode = "VAN14",
            CreatedAt = SaleDate,
            FiscalReceiptNumber = "VAN-0091",
            SalesOrderId = salesOrderId,
            InvoicePayload = """
                {"cardCode":"VAN014","cardName":"Van 14","lines":[
                  {"lineNum":0,"itemCode":"NRI049","quantity":4,"unitPrice":60.00,"warehouseCode":"VAN14"},
                  {"lineNum":1,"itemCode":"CHE011","quantity":2,"unitPrice":12.00,"warehouseCode":"VAN14"}]}
                """
        });

    private ConsolidateDailySalesHandler CreateHandler() =>
        new(
            _context,
            BuildSapClient(),
            BuildBatchValidation(),
            BuildQueueService(),
            StubProxy.For<INotificationService>((_, _) => Task.FromResult(new NotificationDto())),
            ConsolidationDuplicatePostGuardTests.BuildHubContext(),
            ConsolidationDuplicatePostGuardTests.BuildSender(),
            new RecordingAuditService(),
            NullLogger<ConsolidateDailySalesHandler>.Instance);

    private ISAPServiceLayerClient BuildSapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => Task.FromResult<Invoice?>(null),
            nameof(ISAPServiceLayerClient.GetSalesOrderByDocEntryAsync) => ReadOrder(args),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Post(args),
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private Task<SAPSalesOrder?> ReadOrder(object?[]? args)
    {
        _orderReads.Add((int)args![0]!);
        return Task.FromResult(_sapOrder);
    }

    private Task<Invoice> Post(object?[]? args)
    {
        var request = (CreateInvoiceRequest)args![0]!;

        // Copied now: the fallback strips the link from these same line objects before it reposts.
        _posts.Add(request.Lines!
            .Select(line => (line.ItemCode, line.Quantity, line.BaseType, line.BaseEntry, line.BaseLine))
            .ToList());

        if (_posts.Count == 1 && _firstPostFailure is not null)
        {
            throw _firstPostFailure;
        }

        return Task.FromResult(new Invoice { DocEntry = 75001, DocNum = 6120, CardCode = CardCode });
    }

    private static IBatchInventoryValidationService BuildBatchValidation() =>
        StubProxy.For<IBatchInventoryValidationService>((method, _) => method.Name switch
        {
            nameof(IBatchInventoryValidationService.DisregardReservations) => new NoReservationsDisregarded(),
            nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync) =>
                Task.FromResult(new BatchAllocationResult()),
            _ => throw new InvalidOperationException(
                $"IBatchInventoryValidationService.{method.Name} was not expected on this path.")
        });

    private IInvoiceQueueService BuildQueueService() =>
        StubProxy.For<IInvoiceQueueService>((method, _) => method.Name switch
        {
            nameof(IInvoiceQueueService.GetFiscalizedInvoicesAsync) => Task.FromResult(_queueEntries),
            nameof(IInvoiceQueueService.MarkAsConsolidatedAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException(
                $"IInvoiceQueueService.{method.Name} was not expected on this path.")
        });

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is store-generated on Npgsql and has no SQLite
    /// equivalent; see MobileOrderCreditHoldTests.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .IsConcurrencyToken(false)
                .HasDefaultValue(new byte[] { 1 });
        }
    }
}
