using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Covers where an invoice PDF finds the fiscal receipt it prints.
///
/// The fiscal block — QR, verification code, fiscal day, device — went missing from production tax
/// invoices, came back when the same invoice was downloaded again later, and went missing once more.
/// It read as the PDF design regressing, and the design was rebuilt three times over it. The design
/// was never the problem: a till, vending or van sale signs its receipt under the sale's own external
/// reference, hours before SAP assigns a DocNum, while both places the PDF looked for the receipt —
/// the fiscal transaction projection and the device read-back — are keyed on the DocNum. Neither
/// could ever answer, so the block was dropped, silently, from an invoice whose customer is holding
/// the receipt.
/// </summary>
public sealed class InvoicePdfFiscalDetailTests : IDisposable
{
    private const string SaleQr = "https://fdms.zimra.co.zw/000002286211092026000021687760A74CD961202377";
    private const string DeviceQr = "https://fdms.zimra.co.zw/000009999911092026000099999990000000000000000";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public InvoicePdfFiscalDetailTests()
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

    /// <summary>
    /// The case the whole fix exists for, and it holds with the device unreachable — the receipt is in
    /// our own database, so printing it must not depend on anything answering.
    /// </summary>
    [Fact]
    public async Task A_per_sale_invoice_prints_its_sales_receipt_although_the_device_cannot_answer()
    {
        SeedFiscalisedSale(sapDocNum: 6001);
        var invoice = new InvoiceDto { DocNum = 6001, DocEntry = 7001, IsFiscalized = true };

        var qrCode = await ResolveAsync(invoice, SilentDevice);

        Assert.Equal(SaleQr, qrCode);
        Assert.Equal("60A74CD961202377", invoice.FiscalVerificationCode);
        Assert.Equal("128", invoice.FiscalDay);
        Assert.Equal("22862", invoice.FiscalDeviceId);
        Assert.Equal(21687, invoice.FiscalReceiptGlobalNo);
    }

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    [InlineData(SaleSourceSystems.VanSales)]
    public async Task Every_one_invoice_per_sale_route_is_covered(string sourceSystem)
    {
        SeedFiscalisedSale(sapDocNum: 6100, sourceSystem: sourceSystem);
        var invoice = new InvoiceDto { DocNum = 6100, DocEntry = 7100, IsFiscalized = true };

        Assert.Equal(SaleQr, await ResolveAsync(invoice, SilentDevice));
    }

    /// <summary>An invoice that is not the record of a sale is unaffected: the device still answers for it.</summary>
    [Fact]
    public async Task An_ordinary_invoice_still_asks_the_device()
    {
        var invoice = new InvoiceDto { DocNum = 6002, DocEntry = 7002, IsFiscalized = true };

        var qrCode = await ResolveAsync(invoice, AnsweringDevice);

        Assert.Equal(DeviceQr, qrCode);
        Assert.Equal("DEVICE-CODE", invoice.FiscalVerificationCode);
    }

    /// <summary>
    /// A sale that never fiscalised has no receipt, so it must not stand in for one — its invoice is
    /// still fiscalisable by hand, and the device is the only thing that can speak for it.
    /// </summary>
    [Fact]
    public async Task A_sale_that_never_fiscalised_contributes_nothing()
    {
        SeedFiscalisedSale(sapDocNum: 6003, fiscalisation: DesktopSaleFiscalizationStatus.Failed);
        var invoice = new InvoiceDto { DocNum = 6003, DocEntry = 7003 };

        Assert.Null(await ResolveAsync(invoice, SilentDevice));
        Assert.Null(invoice.FiscalVerificationCode);
    }

    /// <summary>
    /// A fiscalised sale whose receipt columns are empty is not an answer. Treating the row itself as
    /// the answer would stop the device ever being asked, which is worse than the bug being fixed.
    /// </summary>
    [Fact]
    public async Task A_sale_row_holding_no_receipt_falls_through_to_the_device()
    {
        SeedFiscalisedSale(sapDocNum: 6004, qrCode: null, verificationCode: null);
        var invoice = new InvoiceDto { DocNum = 6004, DocEntry = 7004, IsFiscalized = true };

        Assert.Equal(DeviceQr, await ResolveAsync(invoice, AnsweringDevice));
    }

    /// <summary>A receipt the caller already holds is printed as given, without a lookup of any kind.</summary>
    [Fact]
    public async Task The_callers_own_receipt_wins()
    {
        SeedFiscalisedSale(sapDocNum: 6005);
        var invoice = new InvoiceDto { DocNum = 6005, DocEntry = 7005, IsFiscalized = true };

        Assert.Equal("CALLER-QR", await ResolveAsync(invoice, ThrowingDevice, requestedQrCode: "CALLER-QR"));
    }

    /// <summary>
    /// A DocNum the projection did answer for keeps its answer: that row is the receipt filed against
    /// this invoice, and the sale row is only consulted because it usually cannot be found.
    /// </summary>
    [Fact]
    public async Task A_receipt_already_projected_onto_the_invoice_wins_over_the_sale_row()
    {
        SeedFiscalisedSale(sapDocNum: 6006);
        var invoice = new InvoiceDto
        {
            DocNum = 6006,
            DocEntry = 7006,
            IsFiscalized = true,
            FiscalQrCode = "PROJECTED-QR"
        };

        Assert.Equal("PROJECTED-QR", await ResolveAsync(invoice, ThrowingDevice));
    }

    /// <summary>
    /// A device that throws is still only a failed lookup, not an absent receipt, and it must not take
    /// the download down with it.
    /// </summary>
    [Fact]
    public async Task A_device_that_throws_leaves_the_invoice_without_a_receipt_rather_than_failing()
    {
        var invoice = new InvoiceDto { DocNum = 6007, DocEntry = 7007, IsFiscalized = true };

        Assert.Null(await ResolveAsync(invoice, ThrowingDevice));
    }

    /// <summary>Cancellation is the caller giving up, and is never swallowed as a failed lookup.</summary>
    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var invoice = new InvoiceDto { DocNum = 6008, DocEntry = 7008, IsFiscalized = true };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvoicePdfFiscalDetail.ResolveAsync(
            _context,
            new StubReader((_, token) => throw new OperationCanceledException(token)),
            invoice,
            requestedQrCode: null,
            NullLogger.Instance,
            cancelled.Token));
    }

    private Task<string?> ResolveAsync(
        InvoiceDto invoice,
        IFiscalReceiptReader reader,
        string? requestedQrCode = null)
        => InvoicePdfFiscalDetail.ResolveAsync(
            _context, reader, invoice, requestedQrCode, NullLogger.Instance, CancellationToken.None);

    /// <summary>The device answered nothing at all — the outage this fix has to survive.</summary>
    private static IFiscalReceiptReader SilentDevice =>
        new StubReader((_, _) => Task.FromResult<FiscalReceiptSnapshot?>(null));

    private static IFiscalReceiptReader AnsweringDevice =>
        new StubReader((_, _) => Task.FromResult<FiscalReceiptSnapshot?>(new FiscalReceiptSnapshot(
            IsFiscalised: true,
            ReceiptGlobalNo: 99999,
            QrCode: DeviceQr,
            VerificationCode: "DEVICE-CODE",
            DeviceSerialNumber: "SERIAL-9",
            DeviceId: "99999",
            FiscalDay: "200",
            TimestampUtc: new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc),
            RawResponseJson: null)));

    private static IFiscalReceiptReader ThrowingDevice =>
        new StubReader((_, _) => throw new InvalidOperationException("the device is unreachable"));

    private void SeedFiscalisedSale(
        int sapDocNum,
        string sourceSystem = SaleSourceSystems.ShopTill,
        DesktopSaleFiscalizationStatus fiscalisation = DesktopSaleFiscalizationStatus.Success,
        string? qrCode = SaleQr,
        string? verificationCode = "60A74CD961202377")
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEFSHOP-01-20260911-{sapDocNum}",
            SourceSystem = sourceSystem,
            CardCode = "KEFSHOP-BP",
            DocDate = new DateTime(2026, 9, 11),
            WarehouseCode = "KEFSHOP",
            Currency = "USD",
            TotalAmount = 25m,
            AmountPaid = 25m,
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = fiscalisation,
            FiscalReceiptNumber = "R-771",
            FiscalQRCode = qrCode,
            FiscalVerificationCode = verificationCode,
            FiscalDayNo = "128",
            FiscalDeviceId = 22862,
            ReceiptGlobalNo = 21687,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            SapDocNum = sapDocNum,
            SapDocEntry = sapDocNum + 1000,
            CreatedAt = DateTime.UtcNow,
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private sealed class StubReader(
        Func<int, CancellationToken, Task<FiscalReceiptSnapshot?>> answer) : IFiscalReceiptReader
    {
        public Task<FiscalReceiptSnapshot?> TryLookupAsync(
            int docNum,
            ReceiptType receiptType,
            ILogger logger,
            CancellationToken cancellationToken)
            => answer(docNum, cancellationToken);
    }
}
