using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxCreditReference;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// A credit note against a 15% invoice REVMax filed before the platform took over is filed on the platform,
/// which must cite the original's device, global number and fiscal day. The day is proved from a receipt of
/// ours the device confirms shares the invoice's <c>global - counter</c>, never from REVMax's envelope.
/// </summary>
public sealed class GetRevmaxCreditReferenceTests : IDisposable
{
    private const int DocNum = 40118;

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly Dictionary<string, InvoiceResponse> device = [];

    public GetRevmaxCreditReferenceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        // Invoice 40118 as the vendor's SAP add-on filed it: receipt 219735, 193rd of its day, at 15%.
        device["40118"] = new InvoiceResponse
        {
            Code = "1", Message = "Success", DeviceID = "22862", FiscalDay = "560",
            VerificationCode = "FA61-4C42-F679-520B", QRcode = "https://fdms.zimra.co.zw/qr",
            Data = new InvoiceData
            {
                InvoiceNo = "40118", ReceiptType = "FiscalInvoice", ReceiptGlobalNo = 219735, ReceiptCounter = 193,
                ReceiptCurrency = "usd", ReceiptTotal = 1150m, ReceiptDate = "2026-08-14T12:15:43",
                ReceiptTaxes = [new ReceiptTax { TaxID = 3, TaxPercent = 15m, TaxCode = "A", SalesAmountWithTax = 1150m, TaxAmount = 150m }]
            }
        };
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task The_day_comes_from_a_confirmed_receipt_of_the_same_day_never_the_envelope()
    {
        // 219735 - 193 = 219542. The closer receipt is the next day's; the further one shares the key.
        Neighbour("GRC-FAC-20260814-AAAAAA", 219700, counter: 158, "534");
        Neighbour("GRC-FAC-20260815-BBBBBB", 219740, counter: 2, "535");

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.False(result.IsError);
        var reference = result.Value;
        Assert.Equal(534, reference.FiscalDayNo); // Not the envelope's 560, nor the nearest receipt's 535.
        Assert.Null(reference.FiscalDayUnresolvedReason);
        Assert.Equal(22862, reference.DeviceId);
        Assert.Equal(219735, reference.ReceiptGlobalNo);
        Assert.Equal("USD", reference.ReceiptCurrency);
        Assert.Equal(1150m, reference.ReceiptTotal);
        Assert.Equal(new DateTime(2026, 8, 14, 12, 15, 43), reference.ReceiptDate);
        var tax = Assert.Single(reference.Taxes);
        Assert.Equal((3, 15m, "A"), (tax.TaxId, tax.TaxPercent, tax.TaxCode));
    }

    [Fact]
    public async Task No_receipt_of_the_same_day_returns_the_reference_without_a_day_and_says_why()
    {
        Neighbour("GRC-FAC-20260815-BBBBBB", 219740, counter: 2, "535");

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.False(result.IsError);
        Assert.Null(result.Value.FiscalDayNo);
        Assert.Contains("ZIMRA taxpayer portal", result.Value.FiscalDayUnresolvedReason);
    }

    [Fact]
    public async Task An_invoice_revmax_never_filed_is_not_fiscalised()
    {
        device.Remove("40118");

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.Equal("RevmaxCredit.NotFiscalised", result.FirstError.Code);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
    }

    [Fact]
    public async Task A_busy_device_is_not_read_as_not_fiscalised()
    {
        device["40118"] = new InvoiceResponse { Code = "0", Message = "Init error -1" };

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.Equal("RevmaxCredit.DeviceUnavailable", result.FirstError.Code);
    }

    [Fact]
    public async Task Another_devices_receipt_under_the_same_number_is_refused()
    {
        device["40118"].DeviceID = "3180";

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.Equal("RevmaxCredit.NotOurReceipt", result.FirstError.Code);
        Assert.Contains("device 3180", result.FirstError.Description);
    }

    [Fact]
    public async Task A_receipt_filed_under_a_different_invoice_number_is_refused()
    {
        device["40118"].Data!.InvoiceNo = "VAN005-INV-20260814-E58A14";

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.Equal("RevmaxCredit.NotOurReceipt", result.FirstError.Code);
    }

    [Fact]
    public async Task A_receipt_without_taxes_cannot_be_cited()
    {
        device["40118"].Data!.ReceiptTaxes = [];

        var result = await Handler().Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.Equal("RevmaxCredit.Incomplete", result.FirstError.Code);
        Assert.Contains("taxes", result.FirstError.Description);
    }

    [Fact]
    public async Task Nothing_is_read_once_revmax_is_retired()
    {
        var result = await Handler(enabled: false).Handle(new GetRevmaxCreditReferenceQuery(DocNum), default);

        Assert.Equal("RevmaxCredit.RevmaxDisabled", result.FirstError.Code);
    }

    private void Neighbour(string reference, int global, int counter, string day)
    {
        db.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference, CardCode = "CASH", WarehouseCode = "GRC", Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success, TotalAmount = 1m,
            FiscalDayNo = day, FiscalReceiptNumber = global.ToString(), SourceSystem = "Till",
            CreatedAt = new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();

        device[reference] = new InvoiceResponse
        {
            Code = "1", DeviceID = "22862", FiscalDay = "560", Data = new InvoiceData
            {
                InvoiceNo = $"22862-{reference}", ReceiptType = "FiscalInvoice", ReceiptGlobalNo = global,
                ReceiptCounter = counter, ReceiptCurrency = "USD", ReceiptTotal = 1m
            }
        };
    }

    private GetRevmaxCreditReferenceHandler Handler(bool enabled = true)
    {
        var client = StubProxy.For<IRevmaxClient>((m, args) => m.Name == "GetInvoiceAsync"
            ? Task.FromResult<InvoiceResponse?>(device.GetValueOrDefault((string)args![0]!)
                ?? new InvoiceResponse { Code = "0", Message = "Invoice not Found" })
            : throw new InvalidOperationException($"Unexpected device call {m.Name}"));
        var settings = Options.Create(new RevmaxSettings { Enabled = enabled, DefaultRefDeviceId = 22862 });
        var selection = Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Platform });
        var fiscal = new RevmaxFiscalizationService(client, settings, Options.Create(new TaxSettings()), selection,
            NullLogger<RevmaxFiscalizationService>.Instance);
        var days = new DesktopCreditFiscalDays(db, client, fiscal, settings, NullLogger<DesktopCreditFiscalDays>.Instance);
        return new GetRevmaxCreditReferenceHandler(client, days, settings, NullLogger<GetRevmaxCreditReferenceHandler>.Instance);
    }
}
