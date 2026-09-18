using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// A sale filed while the device could not vouch for its day carries a blank FiscalDayNo, and the credit
/// dialog refused it outright — van sale VAN005-INV-20260917-E58A14 (INV602) on 2026-09-18. The day is
/// recovered only from a receipt the device confirms is of the same day: same <c>global - counter</c>.
/// </summary>
public sealed class DesktopCreditFiscalDayTests : IDisposable
{
    private const string Blank = "VAN005-INV-20260917-E58A14";

    /// <summary>GetInvoice for INV602 as the device answered it on 2026-09-18, signature omitted.</summary>
    private const string Inv602Json = """
        {
          "Code": "1", "Message": "Success",
          "QRcode": "https://fdms.zimra.co.zw/0000022862170920260000219735FA614C42F679520B",
          "VerificationCode": "FA61-4C42-F679-520B", "VerificationLink": "https://fdms.zimra.co.zw",
          "DeviceID": "22862", "DeviceSerialNumber": "8DE6996C0188", "FiscalDay": "535",
          "Data": {
            "receiptType": "FiscalInvoice", "receiptCurrency": "USD", "receiptCounter": 193,
            "receiptGlobalNo": 219735, "invoiceNo": "22862-VAN005-INV-20260917-E58A14", "buyerData": null,
            "receiptNotes": "22862- Van sale, VAN005 | Customer CLAUDETESTSHOP — Claude Test Shop | Ref VAN005-INV-20260917-E58A14 | Sold 2026-09-17 10:15 | Paid Cash",
            "receiptDate": "2026-09-17T12:15:43", "creditDebitNote": null, "receiptLinesTaxInclusive": true,
            "receiptLines": [ { "receiptLineName": "Kefalos Breakfast Muesli 40g Strips", "receiptLineNo": 1,
              "receiptLineQuantity": 1, "receiptLineType": "Sale", "receiptLineTotal": 0.43, "taxID": 515,
              "receiptLineHSCode": "99001000", "receiptLinePrice": 0.43, "taxCode": "A", "taxPercent": 15.5 } ],
            "receiptTaxes": [ { "salesAmountWithTax": 0.43, "taxAmount": 0.06, "taxID": 515, "taxCode": "A", "taxPercent": 15.5 } ],
            "receiptPayments": [ { "moneyTypeCode": "Cash", "paymentAmount": 0.43 } ],
            "receiptTotal": 0.43, "receiptPrintForm": "Receipt48"
          }
        }
        """;

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly Dictionary<string, InvoiceResponse> device = [];

    public DesktopCreditFiscalDayTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        Sale(Blank, 219735, day: null);
        device[Blank] = JsonSerializer.Deserialize<InvoiceResponse>(Inv602Json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task A_blank_day_is_taken_from_a_confirmed_receipt_of_the_same_day_never_the_envelope()
    {
        // 219735 - 193 = 219542. The closer receipt opened the next day; the further one shares the key.
        Neighbour("VAN003-INV-20260917-AAAAAA", 219700, counter: 158, "534");
        Neighbour("GRC-FAC-20260918-BBBBBB", 219740, counter: 2, "535");

        var source = await Gateway().ReadOriginalAsync(BlankSale(), default);

        Assert.Equal(534, source.FiscalDayNo); // Not the envelope's 535, nor the nearest receipt's.
        Assert.Equal(219735, source.ReceiptGlobalNo);
    }

    [Fact]
    public async Task No_receipt_of_the_same_day_leaves_the_credit_refused()
    {
        Neighbour("GRC-FAC-20260918-BBBBBB", 219740, counter: 2, "535");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(BlankSale(), default));
        Assert.Contains("was not recorded", ex.Message);
    }

    [Fact]
    public async Task A_neighbour_the_device_does_not_hold_as_ours_is_not_trusted()
    {
        Neighbour("VAN003-INV-20260917-AAAAAA", 219700, counter: 158, "534");
        device["VAN003-INV-20260917-AAAAAA"].DeviceID = "3180";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(BlankSale(), default));
    }

    [Fact]
    public async Task A_neighbour_whose_recorded_number_the_device_disagrees_with_is_not_trusted()
    {
        Neighbour("VAN003-INV-20260917-AAAAAA", 219700, counter: 158, "534");
        device["VAN003-INV-20260917-AAAAAA"].Data!.ReceiptGlobalNo = 219701;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(BlankSale(), default));
    }

    [Fact]
    public async Task A_handset_signed_receipt_from_another_device_is_never_a_neighbour()
    {
        Neighbour("VAN003-INV-20260917-AAAAAA", 219700, counter: 158, "534");
        db.DesktopSales.Single(s => s.ExternalReferenceId == "VAN003-INV-20260917-AAAAAA").FiscalDeviceId = 30001;
        db.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(BlankSale(), default));
    }

    [Fact]
    public async Task An_original_receipt_without_a_counter_cannot_be_placed()
    {
        Neighbour("VAN003-INV-20260917-AAAAAA", 219700, counter: 158, "534");
        device[Blank].Data!.ReceiptCounter = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(BlankSale(), default));
    }

    [Fact]
    public async Task A_recorded_receipt_number_that_disagrees_with_the_device_names_the_mismatch()
    {
        var sale = BlankSale();
        sale.FiscalDayNo = "534";
        sale.FiscalReceiptNumber = "219734";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().ReadOriginalAsync(sale, default));
        Assert.Contains("(219734)", ex.Message);
        Assert.Contains("219735", ex.Message);
    }

    private DesktopSaleEntity BlankSale() => db.DesktopSales.Single(s => s.ExternalReferenceId == Blank);

    private void Neighbour(string reference, int global, int counter, string day)
    {
        Sale(reference, global, day);
        device[reference] = new InvoiceResponse
        {
            Code = "1", DeviceID = "22862", FiscalDay = "535", Data = new InvoiceData
            {
                InvoiceNo = $"22862-{reference}", ReceiptType = "FiscalInvoice", ReceiptGlobalNo = global,
                ReceiptCounter = counter, ReceiptCurrency = "USD", ReceiptTotal = 1m
            }
        };
    }

    private void Sale(string reference, int global, string? day)
    {
        db.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference, CardCode = "CLAUDETESTSHOP", WarehouseCode = "VAN005", Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success, TotalAmount = 0.43m,
            FiscalDayNo = day, FiscalReceiptNumber = global.ToString(), SourceSystem = "VanSales",
            CreatedAt = new DateTime(2026, 9, 17, 10, 15, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
    }

    private RevmaxDesktopCreditGateway Gateway()
    {
        var client = StubProxy.For<IRevmaxClient>((m, args) => m.Name == "GetInvoiceAsync"
            ? Task.FromResult<InvoiceResponse?>(device.GetValueOrDefault((string)args![0]!)
                ?? new InvoiceResponse { Code = "0", Message = "Invoice not Found" })
            : throw new InvalidOperationException($"Unexpected device call {m.Name}"));
        var settings = Options.Create(new RevmaxSettings { Enabled = true, DefaultRefDeviceId = 22862 });
        var selection = Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Revmax });
        var fiscal = new RevmaxFiscalizationService(client, settings, Options.Create(new TaxSettings()), selection,
            NullLogger<RevmaxFiscalizationService>.Instance);
        var days = new DesktopCreditFiscalDays(db, client, fiscal, settings, NullLogger<DesktopCreditFiscalDays>.Instance);
        return new RevmaxDesktopCreditGateway(client, fiscal, settings, selection,
            StubProxy.For<IDesktopCreditExternalCredits>((_, _) => Task.FromResult(DesktopCreditExternalHistory.None)), days);
    }
}
