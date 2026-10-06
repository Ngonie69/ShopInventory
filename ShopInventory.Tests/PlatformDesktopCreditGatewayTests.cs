using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Features.FiscalPrintForms;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// A till sale the fiscalisation platform filed is credited on the platform, against the lines it was
/// filed with.
/// </summary>
/// <remarks>
/// The platform's lookup returns a receipt's header and not its lines, so the gateway rebuilds them from
/// the sale. These tests file the sale through the real <see cref="DesktopSaleFiscaliser"/> first and
/// hold the rebuilt lines against what was actually sent.
/// </remarks>
public sealed class PlatformDesktopCreditGatewayTests : IDisposable
{
    private const string Reference = "KEF-FAC-20261001-D03B52F2566F";
    private const int Device = 46668;

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly FiscalisationSettings fiscal = new()
    {
        Provider = FiscalisationProvider.Platform, Enabled = true, DefaultTaxId = 515,
        TaxIdMappings = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 515, ["O0"] = 2 },
        DefaultHsCode = "04031000"
    };
    private readonly TaxSettings tax = new()
    {
        VatRate = 0.155m, RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 0.155m, ["O0"] = 0m }
    };

    /// <summary>What the platform was sent, and what its archive answers a check with.</summary>
    private readonly List<SubmitReceiptApiRequest> submitted = [];
    private readonly Dictionary<(string, ReceiptType), FiscalisedReceiptRecordDto> archive = [];
    private Exception? submitFailure;

    public PlatformDesktopCreditGatewayTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = Reference, CardCode = "CIS006", WarehouseCode = "KEFSHOP", Currency = "USD",
            DocDate = new DateTime(2026, 10, 1), SourceSystem = "KefalosShopTill",
            // What the till charged: 5.16 + 16.20 net, and 0.80 VAT on the 5.16.
            TotalAmount = 22.16m, VatAmount = 0.80m,
            Lines =
            [
                // Charged at 15.5%: 1.72 net is 1.9866 gross, filed at that, not at 1.99 a unit.
                new() { LineNum = 0, ItemCode = "CHS001", ItemDescription = "Gouda 1kg", Quantity = 3m, UnitPrice = 1.72m, TaxCode = "O01", WarehouseCode = "KEFSHOP" },
                // Zero-rated, and discounted: 0.60 less 10% is 0.54.
                new() { LineNum = 1, ItemCode = "MLK002", ItemDescription = "Milk 2L", Quantity = 30m, UnitPrice = 0.60m, DiscountPercent = 10m, TaxCode = "O0", WarehouseCode = "KEFSHOP" },
                // Given away: on the receipt, and nothing to credit.
                new() { LineNum = 2, ItemCode = "BAG001", ItemDescription = "Carrier bag", Quantity = 1m, UnitPrice = 0m, TaxCode = "O01", WarehouseCode = "KEFSHOP" }
            ]
        });
        db.SaveChanges();
    }

    private IFiscalisationApiClient Client() => StubProxy.For<IFiscalisationApiClient>((m, args) => m.Name switch
    {
        nameof(IFiscalisationApiClient.SubmitReceiptAsync) => Submit((SubmitReceiptApiRequest)args![0]!),
        nameof(IFiscalisationApiClient.CheckReceiptAsync) => Task.FromResult(Check((string)args![1]!, (ReceiptType)args[2]!)),
        _ => throw new NotSupportedException(m.Name)
    });

    private Task<SubmitReceiptApiResponse> Submit(SubmitReceiptApiRequest request)
    {
        submitted.Add(request);
        if (submitFailure is not null) return Task.FromException<SubmitReceiptApiResponse>(submitFailure);
        return Task.FromResult(new SubmitReceiptApiResponse
        {
            Success = true, DeviceId = Device, FiscalDayNo = 12, ReceiptCounter = 40,
            ReceiptGlobalNo = request.ReceiptType == ReceiptType.CreditNote ? 950 : 901,
            InvoiceNo = request.InvoiceNo!, ReceiptType = request.ReceiptType, ReceiptTotal = PlatformTotal(request)
        });
    }

    private CheckFiscalisedReceiptApiResponse Check(string invoiceNo, ReceiptType type) =>
        archive.TryGetValue((invoiceNo, type), out var record)
            ? new() { IsFiscalised = true, Matches = [record] }
            : new() { IsFiscalised = false };

    /// <summary>The platform's own rule for a tax-inclusive receipt: the sum of its rounded line totals.</summary>
    private static decimal PlatformTotal(SubmitReceiptApiRequest request) =>
        request.Lines.Sum(l => Math.Round(l.Price * l.Quantity, 2, MidpointRounding.AwayFromZero));

    /// <summary>The platform's VAT for a tax-inclusive receipt: per tax, the share of its sales total, rounded.</summary>
    private static decimal PlatformTax(SubmitReceiptApiRequest request) => request.Lines
        .GroupBy(l => (l.TaxId, l.TaxPercent))
        .Where(g => g.Key.TaxPercent > 0m)
        .Sum(g => Math.Round(
            g.Sum(l => Math.Round(l.Price * l.Quantity, 2, MidpointRounding.AwayFromZero)) * g.Key.TaxPercent!.Value
                / (100m + g.Key.TaxPercent.Value),
            2, MidpointRounding.AwayFromZero));

    private FiscalizationService Platform() => new(Client(), new NoConfigCache(), Options.Create(fiscal),
        Options.Create(tax), NullLogger<FiscalizationService>.Instance);

    private PlatformDesktopCreditGateway Gateway() =>
        new(db, Platform(), Client(), Options.Create(fiscal), Options.Create(tax));

    /// <summary>Files the sale exactly as a till does, and archives what was filed.</summary>
    private async Task<SubmitReceiptApiRequest> FileSaleAsync()
    {
        var sale = await db.DesktopSales.Include(s => s.Lines).SingleAsync();
        var fiscaliser = new DesktopSaleFiscaliser(Platform(),
            StubProxy.For<INotificationService>((m, _) => throw new NotSupportedException(m.Name)),
            Options.Create(tax),
            StubProxy.For<IFiscalPrintFormResolver>((_, _) => Task.FromResult(ReceiptPrintForm.Receipt48)),
            NullLogger<DesktopSaleFiscaliser>.Instance);
        await fiscaliser.FiscaliseAsync(sale, default);
        await db.SaveChangesAsync();
        var filed = Assert.Single(submitted);
        archive[(filed.InvoiceNo!, ReceiptType.FiscalInvoice)] = new FiscalisedReceiptRecordDto
        {
            DeviceId = Device, FiscalDayNo = 12, ReceiptGlobalNo = 901, ReceiptCounter = 7, ReceiptId = 7001,
            InvoiceNo = filed.InvoiceNo!, ReceiptCurrency = "USD", ReceiptTotal = PlatformTotal(filed),
            TaxAmount = PlatformTax(filed), ReceiptType = "FiscalInvoice"
        };
        submitted.Clear();
        return filed;
    }

    private async Task<DesktopSaleEntity> SaleAsync() => await db.DesktopSales.AsNoTracking().SingleAsync();

    [Fact]
    public async Task The_credit_offers_the_lines_the_platform_was_sent()
    {
        var filed = await FileSaleAsync();

        var source = await Gateway().ReadOriginalAsync(await SaleAsync(), default);

        Assert.True(source.OnPlatform);
        Assert.Equal((Device, 12, 901, 7001L), (source.DeviceId, source.FiscalDayNo, source.ReceiptGlobalNo, source.ReceiptId!.Value));
        Assert.Equal(PlatformTotal(filed), source.OriginalTotal);
        Assert.Equal(filed.InvoiceNo, source.OriginalFiscalNumber);
        // Numbered by position, the free line left out with its reason.
        Assert.Equal([1, 2], source.Lines.Select(l => l.LineNo));
        Assert.Contains(source.ExcludedLines!, reason => reason.Contains("Carrier bag"));
        for (var i = 0; i < source.Lines.Count; i++)
        {
            var (line, sent) = (source.Lines[i], filed.Lines[i]);
            Assert.Equal((sent.Name, sent.Quantity, sent.Price, sent.TaxId, sent.TaxPercent, sent.HsCode),
                (line.Name, line.Quantity, line.UnitPrice, line.TaxId, line.TaxPercent, line.HsCode));
        }
        Assert.Equal(1.9866m, source.Lines[0].UnitPrice);
        Assert.Equal(0.54m, source.Lines[1].UnitPrice);
        // Filed at what the till charged, not at 3 x 1.99 + 30 x 0.54 = 22.17.
        Assert.Equal(22.16m, source.OriginalTotal);
    }

    [Fact]
    public async Task A_receipt_filed_at_unit_prices_in_cents_is_still_credited_at_those_prices()
    {
        // Every receipt filed before 6 October 2026 multiplied out a unit price rounded to the cent.
        var filed = await FileSaleAsync();
        var record = archive[(filed.InvoiceNo!, ReceiptType.FiscalInvoice)];
        record.ReceiptTotal = 3 * 1.99m + 30 * 0.54m;
        record.TaxAmount = 0.80m;

        var source = await Gateway().ReadOriginalAsync(await SaleAsync(), default);

        Assert.Equal(22.17m, source.OriginalTotal);
        Assert.Equal([1.99m, 0.54m], source.Lines.Select(l => l.UnitPrice));
    }

    [Fact]
    public async Task The_credit_is_filed_on_the_platform_against_the_original_and_reverses_its_lines()
    {
        await FileSaleAsync();
        var gateway = Gateway();
        var source = await gateway.ReadOriginalAsync(await SaleAsync(), default);
        var plan = DesktopCreditPlanner.Build(source,
            new CreateDesktopCreditRequest(Guid.NewGuid().ToString("N"), "Customer return", [new(1, 2m), new(2, 30m)]),
            new Dictionary<int, decimal>(), 0m, DateTime.UtcNow);
        plan.Receipt.Username = Guid.NewGuid().ToString();

        var result = await gateway.SubmitAsync(plan, default);

        Assert.True(result.Success);
        var credit = Assert.Single(submitted);
        Assert.Equal(ReceiptType.CreditNote, credit.ReceiptType);
        Assert.All(credit.Lines, l => Assert.True(l.Price < 0));
        Assert.Equal(-(3.97m + 30 * 0.54m), PlatformTotal(credit));
        Assert.Equal(plan.Amount, -PlatformTotal(credit));
        Assert.Equivalent(new CreditDebitNoteApiRequest { ReceiptID = 7001, DeviceID = Device, FiscalDayNo = 12, ReceiptGlobalNo = 901 },
            credit.CreditDebitNote);
        // The platform dates it under its device lock; a user id without a full name is refused.
        Assert.Null(credit.ReceiptDate);
        Assert.Null(credit.Username);
        Assert.Equal(fiscal.DefaultDeviceId, credit.DeviceId);
        Assert.Equal("Customer return", credit.ReceiptNotes);
    }

    [Fact]
    public async Task Lines_that_no_longer_come_to_the_receipt_total_are_refused_rather_than_credited()
    {
        await FileSaleAsync();
        // The rate charged has changed since the sale was filed, so today's mapping files other prices.
        tax.RatesByTaxCode["O01"] = 0.15m;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Gateway().ReadOriginalAsync(await SaleAsync(), default));

        Assert.Contains("cannot be reproduced", refusal.Message);
    }

    [Fact]
    public async Task A_recorded_receipt_number_the_platform_does_not_hold_is_refused()
    {
        await FileSaleAsync();
        var sale = await SaleAsync();
        sale.FiscalReceiptNumber = "900";

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(sale, default));

        Assert.Contains("does not match the platform's receipt 901", refusal.Message);
    }

    [Fact]
    public async Task A_sale_the_platform_never_filed_reads_as_absent_not_as_a_failure()
    {
        Assert.Null(await Gateway().TryReadOriginalAsync(await SaleAsync(), default));
    }

    [Fact]
    public async Task With_REVMax_retired_a_sale_the_platform_never_filed_is_refused_without_asking_REVMax()
    {
        // A null REVMax gateway throws if it is asked at all.
        var router = new DesktopCreditFiscalRouter(Gateway(), null!, Options.Create(fiscal),
            Options.Create(new RevmaxSettings { Enabled = false }));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await router.ReadOriginalAsync(await SaleAsync(), default));

        Assert.Contains("raise the credit note in SAP", refusal.Message);
        Assert.DoesNotContain("must be enabled", refusal.Message);
    }

    [Fact]
    public async Task A_sale_the_platform_filed_never_reaches_REVMax()
    {
        await FileSaleAsync();
        var router = new DesktopCreditFiscalRouter(Gateway(), null!, Options.Create(fiscal),
            Options.Create(new RevmaxSettings { Enabled = true }));

        var source = await router.ReadOriginalAsync(await SaleAsync(), default);

        Assert.True(source.OnPlatform);
    }

    [Fact]
    public async Task A_saved_credit_already_on_the_platform_is_adopted_rather_than_filed_again()
    {
        await FileSaleAsync();
        var gateway = Gateway();
        var plan = DesktopCreditPlanner.Build(await gateway.ReadOriginalAsync(await SaleAsync(), default),
            new CreateDesktopCreditRequest(Guid.NewGuid().ToString("N"), "Customer return", [new(1, 1m)]),
            new Dictionary<int, decimal>(), 0m, DateTime.UtcNow);
        Assert.Null(await gateway.FindAsync(plan, default));
        archive[(plan.Receipt.InvoiceNo!, ReceiptType.CreditNote)] = new FiscalisedReceiptRecordDto
        {
            DeviceId = Device, FiscalDayNo = 12, ReceiptGlobalNo = 951, InvoiceNo = plan.Receipt.InvoiceNo!,
            ReceiptCurrency = "USD", ReceiptTotal = -plan.Amount, ReceiptType = "CreditNote"
        };

        var found = await gateway.FindAsync(plan, default);

        Assert.True(found!.Success);
        Assert.False(found.Skipped);
        Assert.Equal("951", found.ReceiptGlobalNo);
    }

    /// <summary>
    /// The credit's caller releases a refused credit for another go, and holds one whose outcome is
    /// open. So only an answer the platform gave may come back as a failure; the rest has to throw.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "ValidationFailed", true, "refused")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "FdmsRequestNotSent", true, "refused")]
    [InlineData(HttpStatusCode.Conflict, "DuplicateInFlight", true, "reconcile")]
    [InlineData(HttpStatusCode.InternalServerError, null, true, "unknown")]
    [InlineData(HttpStatusCode.BadGateway, null, false, "unknown")]
    public async Task Only_an_answer_the_platform_gave_is_reported_as_a_refusal(
        HttpStatusCode status, string? code, bool problemDocument, string expected)
    {
        await FileSaleAsync();
        var gateway = Gateway();
        var plan = DesktopCreditPlanner.Build(await gateway.ReadOriginalAsync(await SaleAsync(), default),
            new CreateDesktopCreditRequest(Guid.NewGuid().ToString("N"), "Customer return", [new(1, 1m)]),
            new Dictionary<int, decimal>(), 0m, DateTime.UtcNow);
        submitFailure = new FiscalisationApiException(status, code, "no", problemDocument);

        if (expected == "unknown")
        {
            await Assert.ThrowsAsync<FiscalisationApiException>(() => gateway.SubmitAsync(plan, default));
            return;
        }
        var result = await gateway.SubmitAsync(plan, default);

        Assert.False(result.Success);
        Assert.Equal(expected == "reconcile", result.RequiresReconciliation);
    }

    private sealed class NoConfigCache : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<FiscalConfigApiResponse?>(null);
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
