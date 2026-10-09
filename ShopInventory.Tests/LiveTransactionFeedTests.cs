using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.LiveTransactions.Queries.GetLiveTransactionFeed;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the live transactions feed: one timeline out of four local tables and the platform's activity
/// feed, a cursor that can never skip a row, and a dashboard that keeps working when the platform does not.
/// </summary>
public sealed class LiveTransactionFeedTests : IDisposable
{
    // 10:00 CAT on 9 October 2026.
    private static readonly DateTime Now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartOfTodayCat = new(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cat = TimeSpan.FromHours(2);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly ManualClock _clock = new();
    private readonly FakeFiscalActivityFeed _fiscal = new();
    private readonly FiscalisationSettings _settings = new()
    {
        Provider = FiscalisationProvider.Platform,
        Enabled = true,
        ApiKey = "fsk_test"
    };

    public LiveTransactionFeedTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _clock.Advance(Now - _clock.GetUtcNow().UtcDateTime);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Handle_MergesEverySourceOldestFirst()
    {
        AddSale("POS-1", Now.AddMinutes(-30));
        AddInvoice(5501, Now.AddMinutes(-20));
        AddIncomingPayment(7701, Now.AddMinutes(-10));
        AddMobilePayment("Paid", created: Now.AddMinutes(-5), completed: Now.AddMinutes(-4));
        await _context.SaveChangesAsync();
        _fiscal.Events.Add(FiscalReceipt("2141:1", "POS-1", Now.AddMinutes(-25)));

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        Assert.True(feed.FiscalFeedAvailable);
        Assert.False(feed.HasMore);
        Assert.Equal(
            [LiveTransactionKinds.Sale, LiveTransactionKinds.FiscalReceipt, LiveTransactionKinds.Invoice,
             LiveTransactionKinds.IncomingPayment, LiveTransactionKinds.MobilePayment],
            feed.Events.Select(e => e.Kind));
        Assert.All(feed.Events, e => Assert.Equal(DateTimeKind.Utc, e.OccurredAtUtc.Kind));
    }

    [Fact]
    public async Task Handle_LinksFiscalReceiptToTheSaleItFiscalised()
    {
        var sale = AddSale("POS-1", Now.AddMinutes(-30));
        var invoice = AddInvoice(5501, Now.AddMinutes(-20));
        await _context.SaveChangesAsync();
        _fiscal.Events.Add(FiscalReceipt("2141:1", "POS-1", Now.AddMinutes(-29)));
        _fiscal.Events.Add(FiscalReceipt("2141:2", "5501", Now.AddMinutes(-19)));
        _fiscal.Events.Add(FiscalReceipt("2141:3", "SAP-ONLY-9", Now.AddMinutes(-18)));

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));
        var receipts = feed.Events.Where(e => e.Kind == LiveTransactionKinds.FiscalReceipt).ToList();

        Assert.Equal($"sale:{sale.Id}", receipts[0].LinkedEventId);
        Assert.Equal($"invoice:{invoice.Id}", receipts[1].LinkedEventId);
        Assert.Null(receipts[2].LinkedEventId);
    }

    [Fact]
    public async Task Handle_CappedSourceStopsThePageAtItsLastEvent()
    {
        // Three sales and a later invoice, with a page of two: the invoice must wait. Returning it would
        // move the reader's cursor past the third sale, which would then never be read.
        AddSale("POS-1", Now.AddMinutes(-30));
        AddSale("POS-2", Now.AddMinutes(-29));
        AddSale("POS-3", Now.AddMinutes(-28));
        AddInvoice(5501, Now.AddMinutes(-10));
        await _context.SaveChangesAsync();

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat, Limit: 2));

        Assert.True(feed.HasMore);
        Assert.Equal(["POS-1", "POS-2"], feed.Events.Select(e => e.Reference));

        var next = await HandleAsync(new GetLiveTransactionFeedQuery(feed.Events[^1].OccurredAtUtc, Limit: 2));

        Assert.Contains(next.Events, e => e.Reference == "POS-3");
    }

    [Fact]
    public async Task Handle_PlatformSaysItIsCapped_PageStopsAtItsLastEvent()
    {
        AddSale("POS-1", Now.AddMinutes(-10));
        await _context.SaveChangesAsync();
        _fiscal.Events.Add(FiscalReceipt("2141:1", "X-1", Now.AddMinutes(-40)));
        _fiscal.HasMore = true;

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        Assert.True(feed.HasMore);
        Assert.Equal([LiveTransactionKinds.FiscalReceipt], feed.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Handle_DefaultsToTheStartOfTodayInCat()
    {
        AddSale("YESTERDAY", StartOfTodayCat.AddMinutes(-1));
        AddSale("TODAY", StartOfTodayCat.AddMinutes(1));
        await _context.SaveChangesAsync();

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(null));

        Assert.Equal(["TODAY"], feed.Events.Select(e => e.Reference));
        Assert.Equal(StartOfTodayCat, _fiscal.LastSince?.UtcDateTime);
    }

    [Fact]
    public async Task Handle_KeyLacksScope_LocalEventsStillArriveWithTheReason()
    {
        AddSale("POS-1", Now.AddMinutes(-10));
        await _context.SaveChangesAsync();
        _fiscal.Failure = new FiscalisationApiException(HttpStatusCode.Forbidden, "Forbidden", "no scope");

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        Assert.False(feed.FiscalFeedAvailable);
        Assert.Contains("activity.read", feed.FiscalFeedMessage);
        Assert.Equal(["POS-1"], feed.Events.Select(e => e.Reference));
    }

    [Fact]
    public async Task Handle_PlatformWithoutTheRoute_SaysItNeedsANewerRelease()
    {
        _fiscal.Failure = new FiscalisationApiException(HttpStatusCode.NotFound, null, "Not Found", hasProblemDocument: false);

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        Assert.False(feed.FiscalFeedAvailable);
        Assert.Contains("newer release", feed.FiscalFeedMessage);
    }

    [Fact]
    public async Task Handle_PlatformUnreachable_LocalEventsStillArrive()
    {
        AddSale("POS-1", Now.AddMinutes(-10));
        await _context.SaveChangesAsync();
        _fiscal.Failure = new HttpRequestException("connection refused");

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        Assert.False(feed.FiscalFeedAvailable);
        Assert.Single(feed.Events);
    }

    [Fact]
    public async Task Handle_RevmaxInstallation_DoesNotAskThePlatform()
    {
        _settings.Provider = FiscalisationProvider.Revmax;

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        Assert.False(feed.FiscalFeedAvailable);
        Assert.Equal(0, _fiscal.Calls);
    }

    [Fact]
    public async Task Handle_MobilePaymentCompletingInsideTheWindow_ShowsAtCompletionAsANewEvent()
    {
        AddMobilePayment("Paid", created: StartOfTodayCat.AddHours(-3), completed: Now.AddMinutes(-1));
        await _context.SaveChangesAsync();

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        var payment = Assert.Single(feed.Events);
        Assert.Equal(Now.AddMinutes(-1), payment.OccurredAtUtc);
        Assert.EndsWith(":Paid", payment.EventId);
    }

    [Fact]
    public async Task Handle_FailedFiscalisationOnASale_IsAFailure()
    {
        var sale = AddSale("POS-1", Now.AddMinutes(-10));
        sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed;
        sale.FiscalError = new string('x', 500);
        await _context.SaveChangesAsync();

        var feed = await HandleAsync(new GetLiveTransactionFeedQuery(StartOfTodayCat));

        var only = Assert.Single(feed.Events);
        Assert.True(only.IsFailure);
        Assert.Equal("Failed", only.FiscalStatus);
        Assert.Equal(GetLiveTransactionFeedHandler.DetailMaxLength + 1, only.Detail!.Length);
    }

    [Fact]
    public void MapFiscalEvent_FiscalDay_ReadsAsDayAndAction()
    {
        var mapped = GetLiveTransactionFeedHandler.MapFiscalEvent(new FiscalActivityEventApiDto
        {
            EventId = "day:7:1",
            Kind = "FiscalDay",
            OccurredAt = new DateTimeOffset(2026, 10, 9, 6, 0, 0, Cat),
            DeviceId = 2141,
            FiscalDayNo = 88,
            Action = "Close",
            Status = "Failed",
            IsFailure = true,
            ErrorCode = "CountersMismatch"
        });

        Assert.Equal(LiveTransactionKinds.FiscalDay, mapped.Kind);
        Assert.Equal("Day 88", mapped.Reference);
        Assert.Equal("Close Failed", mapped.Status);
        Assert.Equal("CountersMismatch", mapped.Detail);
        Assert.Equal(new DateTime(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc), mapped.OccurredAtUtc);
        Assert.Equal("fiscal:day:7:1", mapped.EventId);
    }

    [Theory]
    [InlineData(-8, 300, false)]
    [InlineData(-6, 300, true)]
    [InlineData(-1, 0, false)]
    [InlineData(-1, 1001, false)]
    public void Validator_BoundsTheWindowAndPage(int daysAgo, int limit, bool valid)
    {
        var validator = new GetLiveTransactionFeedValidator(_clock);

        var result = validator.Validate(new GetLiveTransactionFeedQuery(Now.AddDays(daysAgo), limit));

        Assert.Equal(valid, result.IsValid);
    }

    private async Task<LiveTransactionFeedDto> HandleAsync(GetLiveTransactionFeedQuery query)
    {
        var handler = new GetLiveTransactionFeedHandler(
            _context,
            _fiscal,
            Options.Create(_settings),
            _clock,
            NullLogger<GetLiveTransactionFeedHandler>.Instance);

        var result = await handler.Handle(query, CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private DesktopSaleEntity AddSale(string reference, DateTime createdAt)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "Till",
            CardCode = "CASH",
            CardName = "Cash customer",
            DocDate = createdAt.Date,
            TotalAmount = 45m,
            Currency = "USD",
            WarehouseCode = "SHOP01",
            CreatedAt = createdAt
        };
        _context.DesktopSales.Add(sale);
        return sale;
    }

    private InvoiceEntity AddInvoice(int docNum, DateTime createdAt)
    {
        var invoice = new InvoiceEntity
        {
            SAPDocNum = docNum,
            CardCode = "C001",
            CardName = "Customer",
            DocDate = createdAt.Date,
            DocTotal = 310m,
            DocCurrency = "USD",
            Status = "Posted",
            SyncedToSAP = true,
            CreatedAt = createdAt
        };
        _context.Invoices.Add(invoice);
        return invoice;
    }

    private void AddIncomingPayment(int docNum, DateTime createdAt) =>
        _context.IncomingPayments.Add(new IncomingPaymentEntity
        {
            SAPDocNum = docNum,
            CardCode = "C001",
            DocDate = createdAt.Date,
            DocTotal = 100m,
            DocCurrency = "USD",
            Status = "Posted",
            SyncedToSAP = true,
            CreatedAt = createdAt
        });

    private void AddMobilePayment(string status, DateTime created, DateTime? completed) =>
        _context.PaymentTransactions.Add(new PaymentTransaction
        {
            Provider = "PayNow",
            PaymentMethod = "EcoCash",
            Amount = 20m,
            Currency = "USD",
            Status = status,
            CreatedAt = created,
            CompletedAt = completed
        });

    private static FiscalActivityEventApiDto FiscalReceipt(string id, string invoiceNo, DateTime occurredAtUtc) => new()
    {
        EventId = "receipt:" + id,
        Kind = "Receipt",
        OccurredAt = new DateTimeOffset(occurredAtUtc).ToOffset(Cat),
        DeviceId = 2141,
        InvoiceNo = invoiceNo,
        Currency = "USD",
        Total = 45m,
        Status = "Fiscalised"
    };

    private sealed class FakeFiscalActivityFeed : IFiscalActivityFeedClient
    {
        public List<FiscalActivityEventApiDto> Events { get; } = [];
        public bool HasMore { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }

        public Task<FiscalActivityFeedApiResponse> GetFeedAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSince = since;

            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new FiscalActivityFeedApiResponse
            {
                ServerTime = DateTimeOffset.UtcNow,
                HasMore = HasMore,
                Events = Events.Where(e => e.OccurredAt >= since).OrderBy(e => e.OccurredAt).Take(limit).ToList()
            });
        }
    }

    /// <summary>See AdrPerformanceReportTests: SQLite has no store-generated row version.</summary>
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
