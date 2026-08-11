using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// A receipt a van signed for itself reaches ZIMRA by exactly one route: this drain hands it to the
/// fiscalisation platform, the platform archives it, and it goes to ZIMRA in the fiscal day's offline
/// file. A receipt that never gets that far is one the van printed, the customer holds, and ZIMRA closes
/// the day without.
///
/// Everything tested here follows from the chain: each receipt is signed against its predecessor's hash,
/// so they may only be submitted in order, and a failure has to stop that handset rather than skip past
/// it — asking the platform to accept a receipt whose predecessor it does not hold turns one refused
/// upload into a chain break for the rest of the day.
/// </summary>
public sealed class VanSalesSignedReceiptIngestTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingFiscalisationClient _platform = new();

    public VanSalesSignedReceiptIngestTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private VanSalesSignedReceiptIngestService BuildService() =>
        new(
            _context,
            _platform,
            Options.Create(new FiscalisationSettings { Enabled = true }),
            NullLogger<VanSalesSignedReceiptIngestService>.Instance);

    /// <summary>
    /// The request has to describe the receipt exactly as it was signed. The platform re-derives the
    /// signed payload from these fields and refuses anything that hashes differently, so a value
    /// re-derived here rather than reported is a receipt ZIMRA never gets.
    /// </summary>
    [Fact]
    public async Task The_receipt_is_submitted_exactly_as_it_was_signed()
    {
        await SeedAsync(Receipt("VAN006-INV-1", globalNo: 501, counter: 4));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(1, result.Ingested);

        var request = Assert.Single(_platform.Requests);
        Assert.Equal(35410, request.DeviceId);
        Assert.Equal("VAN006-INV-1", request.InvoiceNo);
        Assert.Equal(ReceiptType.FiscalInvoice, request.ReceiptType);
        Assert.Equal("USD", request.Currency);
        Assert.True(request.TaxInclusive);
        Assert.Equal(new DateTime(2026, 8, 10, 11, 30, 0), request.ReceiptDate);
        Assert.Equal(new DateTime(2026, 8, 10, 6, 15, 0), request.FiscalDayOpenedAt);
        Assert.Equal(19, request.FiscalDayNo);
        Assert.Equal(501, request.ReceiptGlobalNo);
        Assert.Equal(4, request.ReceiptCounter);
        Assert.Equal("previous-hash-501", request.PreviousReceiptHash);
        Assert.Equal("hash-501", request.DeviceSignatureHash);
        Assert.Equal("signature-501", request.DeviceSignatureValue);

        // The line as signed: the tax-inclusive unit price, and the tax the lease supplied at signing.
        var line = Assert.Single(request.Lines);
        Assert.Equal("Cheese 1kg", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(50m, line.Price);
        Assert.Equal(517, line.TaxId);
        Assert.Equal(15.5m, line.TaxPercent);
        Assert.Equal("04031000", line.HsCode);

        var stored = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.Ingested, stored.ReceiptIngestStatus);
        Assert.Equal(9001, stored.PlatformReceiptId);
        Assert.NotNull(stored.ReceiptIngestedAt);
        Assert.Null(stored.ReceiptIngestError);
    }

    /// <summary>
    /// Each receipt is signed against the previous one's hash, so the platform accepts N+1 only once it
    /// holds N. Submitting a van's backlog in any order but the order it was signed in would be refused.
    /// </summary>
    [Fact]
    public async Task Receipts_are_submitted_in_signing_order()
    {
        // Seeded out of order on purpose: arrival order is whatever the handset's queue happened to send.
        await SeedAsync(
            Receipt("VAN006-INV-3", globalNo: 503, counter: 6),
            Receipt("VAN006-INV-1", globalNo: 501, counter: 4),
            Receipt("VAN006-INV-2", globalNo: 502, counter: 5));

        await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(
            [501, 502, 503],
            _platform.Requests.Select(request => request.ReceiptGlobalNo).ToArray());
    }

    /// <summary>
    /// A failure stops the handset it belongs to. Skipping to the next receipt would offer the platform
    /// one whose predecessor it does not hold, converting a transient failure into a chain break — and a
    /// chain break cannot be repaired by resending, because the signature is chained.
    /// </summary>
    [Fact]
    public async Task A_failure_stops_that_handset_without_skipping_the_receipts_behind_it()
    {
        await SeedAsync(
            Receipt("VAN006-INV-1", globalNo: 501, counter: 4),
            Receipt("VAN006-INV-2", globalNo: 502, counter: 5),
            Receipt("VAN006-INV-3", globalNo: 503, counter: 6));

        _platform.FailOn("VAN006-INV-2", new FiscalisationApiException(
            HttpStatusCode.ServiceUnavailable, "FdmsRequestNotSent", "The platform is unreachable."));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(1, result.Ingested);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.DevicesStopped);

        // 503 was never offered — it sits behind 502 and waits for it.
        Assert.Equal(["VAN006-INV-1", "VAN006-INV-2"], _platform.Requests.Select(r => r.InvoiceNo!).ToArray());

        var sales = await _context.DesktopSales.OrderBy(s => s.ReceiptGlobalNo).ToListAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.Ingested, sales[0].ReceiptIngestStatus);
        Assert.Equal(DesktopSaleReceiptIngestStatus.Failed, sales[1].ReceiptIngestStatus);
        Assert.Equal(1, sales[1].ReceiptIngestAttempts);

        // Untouched, so the next run offers it again in its place rather than treating it as failed.
        Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sales[2].ReceiptIngestStatus);
        Assert.Equal(0, sales[2].ReceiptIngestAttempts);
    }

    /// <summary>
    /// One van's problem must not hold up another's takings: a device is one chain, and the chains are
    /// independent of each other.
    /// </summary>
    [Fact]
    public async Task One_handsets_failure_does_not_stop_another_handset()
    {
        await SeedAsync(
            Receipt("VAN006-INV-1", globalNo: 501, counter: 4),
            Receipt("VAN007-INV-1", globalNo: 220, counter: 2, deviceNumber: "35411"));

        _platform.FailOn("VAN006-INV-1", new FiscalisationApiException(
            HttpStatusCode.ServiceUnavailable, "FdmsRequestNotSent", "The platform is unreachable."));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(1, result.Ingested);
        Assert.Equal(1, result.Failed);

        var other = await _context.DesktopSales.SingleAsync(s => s.ExternalReferenceId == "VAN007-INV-1");
        Assert.Equal(DesktopSaleReceiptIngestStatus.Ingested, other.ReceiptIngestStatus);
    }

    /// <summary>
    /// A chain break is not a transient failure and must never be retried: the receipt cannot be
    /// re-signed, because amending one invalidates every receipt after it. It is recorded distinctly so
    /// nobody treats it as something a resend will clear.
    /// </summary>
    [Fact]
    public async Task A_chain_break_is_recorded_as_such_and_stops_the_handset()
    {
        await SeedAsync(
            Receipt("VAN006-INV-1", globalNo: 501, counter: 4),
            Receipt("VAN006-INV-2", globalNo: 502, counter: 5));

        _platform.FailOn("VAN006-INV-1", new FiscalisationApiException(
            HttpStatusCode.Conflict, "ChainBreak", "Fiscal day 19 expects receipt counter 3."));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(1, result.ChainBroken);
        Assert.Equal(0, result.Ingested);
        Assert.Single(_platform.Requests);

        var sales = await _context.DesktopSales.OrderBy(s => s.ReceiptGlobalNo).ToListAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.ChainBroken, sales[0].ReceiptIngestStatus);
        Assert.Contains("counter 3", sales[0].ReceiptIngestError);
        Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sales[1].ReceiptIngestStatus);

        // A second run must not offer it again — a chain break waits for a person, not a retry.
        await BuildService().IngestPendingReceiptsAsync();
        Assert.Single(_platform.Requests);
    }

    /// <summary>
    /// A receipt with nothing to verify can never be submitted, and it blocks its device's chain because
    /// its number was still spent on the handset. Saying so on the row is the only way anyone finds out
    /// before the fiscal day closes short.
    /// </summary>
    [Fact]
    public async Task A_receipt_with_no_signature_is_marked_unsubmittable_and_stops_the_handset()
    {
        var unsigned = Receipt("VAN006-INV-1", globalNo: 501, counter: 4);
        unsigned.DeviceSignatureHash = null;
        unsigned.DeviceSignatureValue = null;

        await SeedAsync(unsigned, Receipt("VAN006-INV-2", globalNo: 502, counter: 5));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(1, result.Unsignable);
        Assert.Empty(_platform.Requests);

        var sales = await _context.DesktopSales.OrderBy(s => s.ReceiptGlobalNo).ToListAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.Unsignable, sales[0].ReceiptIngestStatus);
        Assert.Contains("no device signature", sales[0].ReceiptIngestError);
        Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sales[1].ReceiptIngestStatus);
    }

    /// <summary>
    /// A platform build older than this service has no ingest route, and answers with an empty 400 — which
    /// is a deployment problem, not a bad receipt. Spending the receipts' attempts on it would block every
    /// handset within a quarter of an hour and leave each one needing a person to reset it, for a fault
    /// that fixes itself the moment the platform is deployed.
    /// </summary>
    [Fact]
    public async Task A_platform_without_the_ingest_route_costs_no_attempts()
    {
        await SeedAsync(
            Receipt("VAN006-INV-1", globalNo: 501, counter: 4),
            Receipt("VAN006-INV-2", globalNo: 502, counter: 5),
            Receipt("VAN007-INV-1", globalNo: 220, counter: 2, deviceNumber: "35411"));

        // What the live platform actually returns for a route it does not serve: the request never reaches
        // the API-key middleware, so there is no problem document to read.
        _platform.FailOnEverything(new FiscalisationApiException(
            HttpStatusCode.BadRequest, errorCode: null, detail: "Bad Request", hasProblemDocument: false));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.True(result.PlatformEndpointMissing);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Ingested);

        // Tried once. Every handset would fail identically, so the rest are not walked.
        Assert.Single(_platform.Requests);

        var sales = await _context.DesktopSales.ToListAsync();
        Assert.All(sales, sale =>
        {
            Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sale.ReceiptIngestStatus);
            Assert.Equal(0, sale.ReceiptIngestAttempts);
        });
    }

    /// <summary>
    /// The narrowness matters as much as the rule: a refusal the platform explains is a real answer about
    /// this receipt, and it has to keep costing an attempt, or a genuinely bad receipt would be retried
    /// every two minutes for ever.
    /// </summary>
    [Fact]
    public async Task A_refusal_the_platform_explains_still_costs_an_attempt()
    {
        await SeedAsync(Receipt("VAN006-INV-1", globalNo: 501, counter: 4));

        _platform.FailOnEverything(new FiscalisationApiException(
            HttpStatusCode.BadRequest, "ValidationFailed", "HS code is required on a VAT-payer line."));

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.False(result.PlatformEndpointMissing);
        Assert.Equal(1, result.Failed);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.Failed, sale.ReceiptIngestStatus);
        Assert.Equal(1, sale.ReceiptIngestAttempts);
    }

    /// <summary>
    /// Sales the platform already holds, and sales that were never signed offline at all, are not offered
    /// again. Re-offering an archived receipt is harmless — the platform replays it — but a drain that
    /// grows with the table stops finishing.
    /// </summary>
    [Fact]
    public async Task Already_ingested_and_non_van_sales_are_left_alone()
    {
        var ingested = Receipt("VAN006-INV-1", globalNo: 501, counter: 4);
        ingested.ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.Ingested;

        var desktop = Receipt("DESK-1", globalNo: 900, counter: 1);
        desktop.SourceSystem = "Desktop";
        desktop.ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.NotApplicable;

        await SeedAsync(ingested, desktop);

        var result = await BuildService().IngestPendingReceiptsAsync();

        Assert.Equal(0, result.Total);
        Assert.Empty(_platform.Requests);
    }

    private async Task SeedAsync(params DesktopSaleEntity[] sales)
    {
        _context.DesktopSales.AddRange(sales);
        await _context.SaveChangesAsync();
    }

    private static DesktopSaleEntity Receipt(
        string reference,
        int globalNo,
        int counter,
        string deviceNumber = "35410") => new()
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            CardName = "Simbisa",
            DocDate = new DateTime(2026, 8, 10),
            Currency = "USD",
            TotalAmount = 100m,
            VatAmount = 13.42m,
            AmountPaid = 100m,
            PaymentMethod = "Cash",
            WarehouseCode = "VAN006",
            CostCentreCode = "CC006",

            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDeviceNumber = deviceNumber,
            FiscalDayNo = "19",
            ReceiptGlobalNo = globalNo,
            ReceiptCounter = counter,

            ReceiptDate = new DateTime(2026, 8, 10, 11, 30, 0, DateTimeKind.Unspecified),
            FiscalDayOpenedAt = new DateTime(2026, 8, 10, 6, 15, 0, DateTimeKind.Unspecified),
            PreviousReceiptHash = $"previous-hash-{globalNo}",
            DeviceSignatureHash = $"hash-{globalNo}",
            DeviceSignatureValue = $"signature-{globalNo}",
            ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.Pending,

            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheese 1kg",
                    Quantity = 2m,
                    UnitPrice = 50m,
                    LineTotal = 100m,
                    WarehouseCode = "VAN006",
                    TaxCode = "15.5% Output VAT USD",
                    TaxId = 517,
                    TaxPercent = 15.5m,
                    HsCode = "04031000"
                }
            ]
        };

    /// <summary>
    /// Stands in for the fiscalisation platform, recording what it was asked to archive and failing on
    /// demand. Only the ingest call is implemented — reaching any other one from this drain would itself
    /// be the bug.
    /// </summary>
    private sealed class RecordingFiscalisationClient : IFiscalisationApiClient
    {
        private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
        private Exception? _blanketFailure;

        public List<IngestSignedReceiptApiRequest> Requests { get; } = [];

        public void FailOn(string invoiceNo, Exception failure) => _failures[invoiceNo] = failure;

        /// <summary>Fails every call, the way a platform-wide fault does.</summary>
        public void FailOnEverything(Exception failure) => _blanketFailure = failure;

        public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (_blanketFailure is not null)
            {
                throw _blanketFailure;
            }

            if (_failures.TryGetValue(request.InvoiceNo ?? string.Empty, out var failure))
            {
                throw failure;
            }

            return Task.FromResult(new SubmitReceiptApiResponse
            {
                Success = true,
                DeviceId = request.DeviceId,
                FiscalDayNo = request.FiscalDayNo,
                InvoiceNo = request.InvoiceNo ?? string.Empty,
                ReceiptCounter = request.ReceiptCounter,
                ReceiptGlobalNo = request.ReceiptGlobalNo,
                ReceiptId = 9001
            });
        }

        public Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(
            SapFiscaliseReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "A receipt signed on a handset must never be fiscalised again.");

        public Task<SubmitReceiptApiResponse> SubmitReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "A receipt signed on a handset must never be fiscalised again.");

        public Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
            int deviceId, string invoiceNo, ReceiptType receiptType, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No receipt check expected.");

        public Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No config read expected.");

        public Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No status read expected.");
    }
}
