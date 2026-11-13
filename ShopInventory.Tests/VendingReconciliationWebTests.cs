using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Web.Features.Vending;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The portal half of the vending cash-up: the capture form's preview, and the Reconciliation workbook.
/// </summary>
public sealed class VendingReconciliationWebTests
{
    private static VendingCashUpSheetModel NewSheet(decimal usdCash = 162.80m, decimal zigCash = 300m) => new()
    {
        CashierUserId = Guid.NewGuid(),
        CashierName = "Depot till",
        WarehouseCode = "DEPOT1",
        BusinessDate = new DateTime(2026, 11, 10),
        Status = VendingCashUpStatus.NotCashedUp,
        Currencies =
        [
            new() { Currency = "USD", ExpectedCash = usdCash, ExpectedCashSales = 2 },
            new() { Currency = "ZIG", ExpectedCash = zigCash, ExpectedCashSales = 1 }
        ]
    };

    [Fact]
    public void A_form_with_nothing_typed_shows_no_variance_rather_than_a_shortfall_of_the_day()
    {
        var form = VendingCashUpForm.From(NewSheet());

        Assert.False(form.IsComplete);
        Assert.False(form.HasVariance);
        Assert.All(form.Currencies, currency =>
        {
            Assert.Null(currency.Counted);
            Assert.Null(currency.Variance);
        });
    }

    [Fact]
    public void The_preview_takes_the_float_out_and_counts_notes_and_coins()
    {
        var form = VendingCashUpForm.From(NewSheet());
        var usd = form.Currencies.Single(currency => currency.Currency == "USD");
        usd.OpeningFloat = 50m;
        usd.ByNotes = true;
        usd.Notes[100m] = 1;
        usd.Notes[50m] = 2;
        usd.Notes[10m] = 1;
        usd.Coins = 2.80m;
        form.Currencies.Single(currency => currency.Currency == "ZIG").Total = 290m;

        Assert.Equal(212.80m, usd.Counted);
        Assert.Equal(0m, usd.Variance);
        Assert.Equal(162.80m, usd.ToBank);
        Assert.Equal(-10m, form.Currencies.Single(currency => currency.Currency == "ZIG").Variance);
        Assert.True(form.HasVariance);
        Assert.True(form.NeedsReason);

        var request = form.ToRequest(NewSheet());
        var usdLine = request.Currencies.Single(line => line.Currency == "USD");
        Assert.Null(usdLine.CountedCash);
        Assert.Contains(usdLine.Denominations!, note => note.Value == VendingCashUpForm.CoinValue && note.Quantity == 280);
        Assert.Equal(212.80m, usdLine.Denominations!.Sum(note => note.Value * note.Quantity));
        Assert.Equal(290m, request.Currencies.Single(line => line.Currency == "ZIG").CountedCash);
    }

    [Fact]
    public void A_saved_count_by_notes_reopens_with_its_notes_and_coins()
    {
        var sheet = NewSheet();
        sheet.CashUpId = 7;
        sheet.Revision = 1;
        sheet.Status = VendingCashUpStatus.Submitted;
        sheet.Currencies[0].OpeningFloat = 50m;
        sheet.Currencies[0].CountedCash = 212.80m;
        sheet.Currencies[0].Denominations =
        [
            new() { Value = 100m, Quantity = 1 },
            new() { Value = 50m, Quantity = 2 },
            new() { Value = 10m, Quantity = 1 },
            new() { Value = 0.01m, Quantity = 280 }
        ];
        sheet.Currencies[1].OpeningFloat = 0m;
        sheet.Currencies[1].CountedCash = 300m;

        var form = VendingCashUpForm.From(sheet);
        var usd = form.Currencies[0];

        Assert.True(usd.ByNotes);
        Assert.Equal(2.80m, usd.Coins);
        Assert.Equal(212.80m, usd.Counted);
        Assert.Equal(300m, form.Currencies[1].Total);
        Assert.False(form.HasVariance);
    }

    [Fact]
    public void A_tender_left_blank_is_read_as_declared_at_nothing_as_the_API_reads_it()
    {
        var sheet = NewSheet();
        sheet.Tenders = [new() { Currency = "USD", Tender = "Ecocash", Expected = 25m, ExpectedSales = 1 }];
        var form = VendingCashUpForm.From(sheet);
        foreach (var currency in form.Currencies)
        {
            currency.Total = currency.ExpectedCash;
        }

        var ecocash = form.Tenders.Single(tender => tender.Currency == "USD" && tender.Tender == "Ecocash");
        Assert.Equal(-25m, ecocash.Variance);
        Assert.True(form.HasVariance);

        ecocash.Declared = 25m;
        Assert.False(form.HasVariance);
    }

    [Fact]
    public void The_workbook_leaves_an_uncounted_day_blank_and_totals_each_currency_apart()
    {
        var model = new VendingReconciliationModel
        {
            FromDate = new DateTime(2026, 11, 4),
            ToDate = new DateTime(2026, 11, 10),
            Rows =
            [
                new()
                {
                    CashUpId = 1, Revision = 2, Status = VendingCashUpStatus.Approved,
                    BusinessDate = new DateTime(2026, 11, 10), CashierName = "Depot till", CashierUsername = "depot.till",
                    WarehouseCode = "DEPOT1", DepotName = "Test depot", VarianceReason = "Change given twice",
                    CapturedByName = "supervisor", ApprovedByName = "admin.one", ApprovedAt = new DateTime(2026, 11, 10, 18, 0, 0),
                    Currencies =
                    [
                        new() { Currency = "USD", Invoiced = 100m, Sales = 3, ExpectedCash = 100m, OpeningFloat = 20m, CountedCash = 115m, Accounted = 95m, Banked = 95m, Variance = -5m },
                        new() { Currency = "ZIG", Invoiced = 300m, Sales = 1, ExpectedCash = 300m, OpeningFloat = 0m, CountedCash = 300m, Accounted = 300m, Banked = 300m, Variance = 0m }
                    ]
                },
                new()
                {
                    Status = VendingCashUpStatus.NotCashedUp,
                    BusinessDate = new DateTime(2026, 11, 9), CashierName = "Depot till", CashierUsername = "depot.till",
                    WarehouseCode = "DEPOT1", DepotName = "Test depot",
                    Currencies = [new() { Currency = "USD", Invoiced = 250m, Sales = 5, ExpectedCash = 250m }]
                }
            ],
            Counts = new() { CashierDays = 2, NotCashedUp = 1, Approved = 1 }
        };

        var bytes = new ReportExportService().ExportVendingReconciliationToExcel(model, "all depots");

        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
        {
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
            Assert.Empty(errors);
        }

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var ws = workbook.Worksheet("Cash-ups");
        var header = ws.CellsUsed(cell => cell.GetString() == "Variance").First().Address.RowNumber;

        // Columns as declared: 6 Status, 7 Currency, 9 Invoiced, 12 Counted, 14 Banked, 15 Variance.
        var notCounted = ws.Row(header + 3);
        Assert.Equal("Not cashed up", notCounted.Cell(6).GetString());
        Assert.Equal(250m, notCounted.Cell(9).GetValue<decimal>());
        Assert.True(notCounted.Cell(12).IsEmpty());
        Assert.True(notCounted.Cell(14).IsEmpty());
        Assert.True(notCounted.Cell(15).IsEmpty());

        var usd = ws.Row(header + 1);
        Assert.Equal("USD", usd.Cell(7).GetString());
        Assert.Equal(-5m, usd.Cell(15).GetValue<decimal>());
        Assert.Equal("ZiG", ws.Row(header + 2).Cell(7).GetString());

        var totals = ws.RowsUsed().Where(row => row.Cell(6).GetString().StartsWith("Total (")).ToList();
        Assert.Equal(2, totals.Count);
        var usdTotal = totals.Single(row => row.Cell(7).GetString() == "USD");
        Assert.Equal(350m, usdTotal.Cell(9).GetValue<decimal>());
        Assert.Equal(-5m, usdTotal.Cell(15).GetValue<decimal>());
        Assert.Equal(300m, totals.Single(row => row.Cell(7).GetString() == "ZiG").Cell(9).GetValue<decimal>());
    }
}
