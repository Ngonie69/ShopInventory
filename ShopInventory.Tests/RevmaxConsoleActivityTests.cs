using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxActivity;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// What the console's REVMax section counts, and what it must never do to count it.
///
/// The counting rules are the point. A fiscal transaction row is written per attempt, by several
/// writers, for both providers, into one table — so every figure on that section is a decision about
/// which rows belong to it. Counted naively, a document retried twice is three documents, a receipt read
/// back is a second receipt, and USD and ZWG add up to a number that is not money. None of that is
/// visible on the page: it just reads as a larger business.
/// </summary>
public sealed class RevmaxConsoleActivityTests : IDisposable
{
    private const string OurSerial = "8DE6996C0188";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public RevmaxConsoleActivityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
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

    // ── What must never happen ──────────────────────────────────────────────

    [Fact]
    public async Task Reading_the_section_never_calls_ZReport()
    {
        // ZReport closes the taxpayer's fiscal day. It reads like a report and is a write, so a console
        // that called it to render a panel would close the day every time someone opened the page.
        var client = new ReadOnlyRevmaxClient();

        await Handler(client).Handle(new GetRevmaxActivityQuery(), CancellationToken.None);

        Assert.False(client.ZReportCalled);
    }

    // ── Counting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_document_filed_after_two_failures_counts_once_and_as_filed()
    {
        // Every attempt writes a row. Counting rows would report one invoice as three, and would report
        // it as both filed and unfiled at the same time.
        await SeedAsync(771149, "Failed", syncedAt: At(9, 0));
        await SeedAsync(771149, "Failed", syncedAt: At(9, 5));
        await SeedAsync(771149, "Success", receiptGlobalNo: 216230, syncedAt: At(9, 10));

        var result = await ActivityAsync();

        Assert.Equal(1, result.Totals.DocumentsFiled);
        Assert.Equal(0, result.Totals.DocumentsFailed);
    }

    [Fact]
    public async Task A_document_that_failed_last_counts_as_unfiled()
    {
        await SeedAsync(771150, "Success", receiptGlobalNo: 216231, syncedAt: At(9, 0));
        await SeedAsync(771150, "Failed", syncedAt: At(9, 30));

        var result = await ActivityAsync();

        Assert.Equal(0, result.Totals.DocumentsFiled);
        Assert.Equal(1, result.Totals.DocumentsFailed);
    }

    [Fact]
    public async Task One_receipt_read_back_twice_is_one_receipt()
    {
        // Reading a filed receipt back writes another row carrying the same receipt number. It is the
        // same receipt at ZIMRA, and counting it twice would overstate what was filed.
        await SeedAsync(769617, "Success", receiptGlobalNo: 216192, syncedAt: At(8, 0));
        await SeedAsync(769617, "Success", receiptGlobalNo: 216192, syncedAt: At(8, 40));

        var result = await ActivityAsync();

        Assert.Equal(1, result.Totals.ReceiptsFiled);
        Assert.Equal(216192, result.Totals.FirstReceiptGlobalNo);
        Assert.Equal(216192, result.Totals.LastReceiptGlobalNo);
    }

    [Fact]
    public async Task Currencies_are_never_added_together()
    {
        // This taxpayer trades in more than one currency. A single total across them is not a large
        // number, it is a wrong one.
        await SeedAsync(1, "Success", receiptGlobalNo: 11, currency: "USD", docTotal: 100m, vatSum: 15.5m);
        await SeedAsync(2, "Success", receiptGlobalNo: 12, currency: "USD", docTotal: 50m, vatSum: 7.75m);
        await SeedAsync(3, "Success", receiptGlobalNo: 13, currency: "ZWG", docTotal: 4000m, vatSum: 620m);

        var result = await ActivityAsync();

        var usd = Assert.Single(result.ByCurrency, total => total.Currency == "USD");
        var zwg = Assert.Single(result.ByCurrency, total => total.Currency == "ZWG");

        Assert.Equal(150m, usd.DocTotal);
        Assert.Equal(23.25m, usd.VatSum);
        Assert.Equal(2, usd.Documents);
        Assert.Equal(4000m, zwg.DocTotal);
    }

    [Fact]
    public async Task Rows_outside_the_window_are_not_counted()
    {
        await SeedAsync(500, "Success", receiptGlobalNo: 900, syncedAt: At(9, 0).AddDays(-90));
        await SeedAsync(501, "Success", receiptGlobalNo: 901, syncedAt: At(9, 0));

        var result = await ActivityAsync();

        Assert.Equal(1, result.Totals.DocumentsFiled);
        Assert.Equal(901, result.Totals.FirstReceiptGlobalNo);
    }

    // ── Attribution ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_devices_receipts_are_excluded_when_the_device_names_its_serial()
    {
        // The serial is the only thing on a row that says which device filed it. Without this filter the
        // section would report another provider's filing as REVMax's.
        await SeedAsync(600, "Success", receiptGlobalNo: 1000, serial: OurSerial);
        await SeedAsync(601, "Success", receiptGlobalNo: 1001, serial: "SOMEONE-ELSES-BOX");

        var result = await ActivityAsync();

        Assert.Equal(OurSerial, result.AttributedToSerial);
        Assert.Equal(1, result.Totals.DocumentsFiled);
        Assert.Equal(1, result.Totals.ReceiptsFiled);
    }

    [Fact]
    public async Task A_failure_that_never_reached_the_device_is_still_shown()
    {
        // A filing that failed has no serial, because the device never stamped it. Filtering strictly on
        // the serial would hide every failure — the half of this section someone is most likely to have
        // opened it for.
        await SeedAsync(700, "Failed", serial: null);

        var result = await ActivityAsync();

        Assert.Equal(1, result.Totals.DocumentsFailed);
        Assert.Single(result.Recent);
        Assert.False(result.Recent[0].Filed);
    }

    [Fact]
    public async Task An_unreachable_device_still_reports_the_filing_and_says_so()
    {
        // The figures come from our own database and are perfectly readable while the device is down.
        // Blanking the section would turn a device outage into an apparent absence of trading.
        await SeedAsync(800, "Success", receiptGlobalNo: 1200, serial: OurSerial);

        var result = await ActivityAsync(new ReadOnlyRevmaxClient { Silent = true });

        Assert.Null(result.AttributedToSerial);
        Assert.NotNull(result.DeviceError);
        Assert.Equal(1, result.Totals.DocumentsFiled);
    }

    [Fact]
    public async Task A_device_that_throws_does_not_fail_the_section()
    {
        await SeedAsync(801, "Success", receiptGlobalNo: 1201);

        var result = await ActivityAsync(new ReadOnlyRevmaxClient { Throw = true });

        Assert.NotNull(result.DeviceError);
        Assert.Equal(1, result.Totals.DocumentsFiled);
    }

    // ── The provider ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_section_says_when_REVMax_is_not_the_live_provider()
    {
        var result = await ActivityAsync(provider: FiscalisationProvider.Platform);

        Assert.False(result.IsLiveProvider);
        Assert.Equal("Platform", result.Provider);
    }

    [Fact]
    public async Task An_inverted_window_is_refused_rather_than_answered_empty()
    {
        // An empty answer to a mistyped date reads as "nothing was filed", which on this page is a
        // statement about compliance rather than about the dates.
        var handler = Handler(new ReadOnlyRevmaxClient());

        var result = await handler.Handle(
            new GetRevmaxActivityQuery(FromDate: At(9, 0), ToDate: At(9, 0).AddDays(-5)),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static DateTime At(int hour, int minute) =>
        new(2026, 9, 10, hour, minute, 0, DateTimeKind.Utc);

    private async Task<RevmaxActivityResult> ActivityAsync(
        ReadOnlyRevmaxClient? client = null,
        FiscalisationProvider provider = FiscalisationProvider.Revmax)
    {
        var result = await Handler(client ?? new ReadOnlyRevmaxClient(), provider).Handle(
            // A window wide enough to hold the fixtures and narrow enough to exclude the row that is
            // deliberately outside it.
            new GetRevmaxActivityQuery(FromDate: At(0, 0).AddDays(-5), ToDate: At(0, 0).AddDays(1)),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private GetRevmaxActivityHandler Handler(
        IRevmaxClient client,
        FiscalisationProvider provider = FiscalisationProvider.Revmax)
        => new(
            _context,
            client,
            new StaticOptionsMonitor<RevmaxSettings>(new RevmaxSettings { Enabled = true }),
            new StaticOptionsMonitor<FiscalisationSettings>(new FiscalisationSettings { Provider = provider }),
            NullLogger<GetRevmaxActivityHandler>.Instance);

    private async Task SeedAsync(
        int docNum,
        string status,
        int? receiptGlobalNo = null,
        string? serial = OurSerial,
        string currency = "USD",
        decimal docTotal = 100m,
        decimal vatSum = 15.5m,
        DateTime? syncedAt = null,
        string documentType = "Invoice")
    {
        var stamp = syncedAt ?? At(9, 0);

        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = $"{documentType}-{docNum}-{stamp.Ticks}",
            DocumentType = documentType,
            DocNum = docNum,
            Status = status,
            ReceiptGlobalNo = receiptGlobalNo,
            DeviceSerialNumber = serial,
            FiscalDay = "524",
            CardCode = "KEF001",
            CardName = "Kefalos Wholesale",
            DocTotal = docTotal,
            VatSum = vatSum,
            Currency = currency,
            TimestampUtc = stamp,
            LastSyncedAtUtc = stamp
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// A device that answers the three read routes and fails the test if the day is closed.
    /// </summary>
    /// <remarks>
    /// <see cref="ZReportAsync"/> records rather than throws so the assertion names what happened. A
    /// throw here would surface as the handler's own caught device error and read as a passing test.
    /// </remarks>
    private sealed class ReadOnlyRevmaxClient : IRevmaxClient
    {
        public bool ZReportCalled { get; private set; }

        /// <summary>The device does not answer at all.</summary>
        public bool Silent { get; init; }

        /// <summary>The device is unreachable at the socket.</summary>
        public bool Throw { get; init; }

        public Task<CardDetailsResponse?> GetCardDetailsAsync(CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new HttpRequestException("No route to host");
            }

            return Task.FromResult<CardDetailsResponse?>(Silent ? null : new CardDetailsResponse
            {
                Code = "1",
                DeviceID = "22862",
                DeviceSerialNumber = OurSerial,
                Data = new CardDetailsData
                {
                    COMPANYNAME = "Kefalos Cheese Products (Pvt) Ltd",
                    TIN = "2000022395"
                }
            });
        }

        public Task<DayStatusResponse?> GetDayStatusAsync(CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new HttpRequestException("No route to host");
            }

            return Task.FromResult<DayStatusResponse?>(Silent ? null : new DayStatusResponse
            {
                Code = "1",
                DeviceID = "22862",
                DeviceSerialNumber = OurSerial,
                Data = new DayStatusData
                {
                    FiscalDayStatus = "FiscalDayOpened",
                    LastFiscalDayNo = 524,
                    LastReceiptGlobalNo = 216080
                }
            });
        }

        public Task<LicenseResponse?> GetLicenseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<LicenseResponse?>(Throw || Silent ? null : new LicenseResponse { Code = "1" });

        public Task<ZReportResponse?> GetZReportAsync(CancellationToken cancellationToken = default)
        {
            ZReportCalled = true;
            return Task.FromResult<ZReportResponse?>(null);
        }

        public Task<LicenseResponse?> SetLicenseAsync(string license, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The console must never write a licence.");

        public Task<InvoiceResponse?> GetInvoiceAsync(string invoiceNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<InvoiceResponse?>(null);

        public Task<UnprocessedInvoicesSummaryResponse?> GetUnprocessedInvoicesSummaryAsync(
            string? fiscalDayNumber = null,
            string? fiscalDate = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<UnprocessedInvoicesSummaryResponse?>(null);

        public Task<TransactMResponse?> TransactMAsync(
            TransactMRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The console must never file a receipt.");

        public Task<TransactMExtResponse?> TransactMExtAsync(
            TransactMExtRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The console must never file a receipt.");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
