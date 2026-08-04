using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the report that answers "how much did we actually move" in volume rather
/// than in units.
/// </summary>
/// <remarks>
/// Two things here are easy to get wrong and expensive to get wrong quietly.
///
/// The first is the netting. SAP states a credit memo in positive quantities, so a
/// report that adds both sides reports a month with heavy returns as its best month.
/// The handler negates credit lines on the way in; these tests hold that.
///
/// The second is the item with no conversion factor. Multiplying its quantity by a
/// missing factor gives zero, and a zero disappears into a SUM with nothing on the
/// page to say the total is short. The handler reports those items separately and
/// leaves them out of the volume; that is asserted rather than assumed.
/// </remarks>
public sealed class ItemVolumeSalesReportTests : IDisposable
{
    private const string From = "2026-07-01";
    private const string To = "2026-07-31";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ItemVolumeSalesReportTests()
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

    [Fact]
    public async Task Credited_quantity_is_deducted_before_the_volume_is_converted()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5001, "CIS006", "2026-07-04", ("YOG143", 100m, 250m))],
            creditNotes: [CreditNote(9, 7001, "CIS006", "2026-07-18", ("YOG143", 10m, 25m))]);

        var result = await handler.Handle(Query(), default);

        Assert.False(result.IsError);
        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal(100m, item.InvoicedQuantity);
        Assert.Equal(10m, item.CreditedQuantity);
        Assert.Equal(90m, item.NetQuantity);
        // 90 units at 0.6 litres each, not 110 and not 100.
        Assert.Equal(54m, item.NetVolume);
        Assert.Equal(225m, item.NetRevenueUsd);
    }

    [Fact]
    public async Task A_month_credited_beyond_what_it_invoiced_reports_negative_rather_than_zero()
    {
        // Returns against an invoice raised before the window are real and the report
        // must not floor them at zero — a rep whose month went backwards should see it.
        await SeedFactorAsync("BUT015", 4.8m);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5002, "CIS006", "2026-07-04", ("BUT015", 5m, 40m))],
            creditNotes: [CreditNote(9, 7002, "CIS006", "2026-07-20", ("BUT015", 12m, 96m))]);

        var result = await handler.Handle(Query(), default);

        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal(-7m, item.NetQuantity);
        Assert.Equal(-33.6m, item.NetVolume);
        Assert.Equal(-56m, item.NetRevenueUsd);
    }

    [Fact]
    public async Task An_item_with_no_factor_is_reported_unconverted_rather_than_as_zero_volume()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var handler = CreateHandler(
            invoices:
            [
                Invoice(1, 5003, "CIS006", "2026-07-04", ("YOG143", 100m, 250m), ("NEW001", 40m, 80m))
            ],
            creditNotes: []);

        var result = await handler.Handle(Query(), default);

        var unmapped = result.Value.ItemTotals.Single(item => item.ItemCode == "NEW001");
        Assert.Null(unmapped.VolumeFactor);
        Assert.False(unmapped.HasVolumeFactor);
        Assert.Equal(40m, unmapped.NetQuantity);
        Assert.Equal(0m, unmapped.NetVolume);

        Assert.Equal(["NEW001"], result.Value.ItemCodesWithoutFactor);
        Assert.Equal(1, result.Value.Summary.ItemsWithoutFactorCount);
        Assert.Equal(40m, result.Value.Summary.QuantityWithoutFactor);
        // The converted item's volume stands alone; the unconvertible 40 units are not
        // rolled in as zero litres without saying so.
        Assert.Equal(60m, result.Value.Summary.NetVolume);
        Assert.Equal(140m, result.Value.Summary.NetQuantity);
    }

    [Fact]
    public async Task A_retired_factor_stops_converting_without_hiding_the_item()
    {
        await SeedFactorAsync("YOG143", 0.6m, isActive: false);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5004, "CIS006", "2026-07-04", ("YOG143", 100m, 250m))],
            creditNotes: []);

        var result = await handler.Handle(Query(), default);

        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal(100m, item.NetQuantity);
        Assert.False(item.HasVolumeFactor);
        Assert.Equal(0m, result.Value.Summary.NetVolume);
    }

    [Fact]
    public async Task Choosing_items_narrows_both_the_invoices_and_the_credit_notes()
    {
        await SeedFactorAsync("YOG143", 0.6m);
        await SeedFactorAsync("BUT015", 4.8m);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5005, "CIS006", "2026-07-04", ("YOG143", 100m, 250m), ("BUT015", 20m, 160m))],
            creditNotes: [CreditNote(9, 7005, "CIS006", "2026-07-09", ("YOG143", 10m, 25m), ("BUT015", 5m, 40m))]);

        var result = await handler.Handle(Query(itemCodes: ["BUT015"]), default);

        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal("BUT015", item.ItemCode);
        Assert.Equal(15m, item.NetQuantity);
        Assert.Equal(72m, item.NetVolume);
    }

    [Fact]
    public async Task Cancelled_documents_count_for_nothing_on_either_side()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var cancelledInvoice = Invoice(2, 5006, "CIS006", "2026-07-05", ("YOG143", 500m, 1250m));
        cancelledInvoice.Cancelled = "tYES";
        var cancelledCredit = CreditNote(8, 7006, "CIS006", "2026-07-06", ("YOG143", 50m, 125m));
        cancelledCredit.Cancelled = "tYES";

        var handler = CreateHandler(
            invoices: [Invoice(1, 5007, "CIS006", "2026-07-04", ("YOG143", 100m, 250m)), cancelledInvoice],
            creditNotes: [cancelledCredit]);

        var result = await handler.Handle(Query(), default);

        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal(100m, item.NetQuantity);
        Assert.Equal(0m, item.CreditedQuantity);
        Assert.Equal(1, result.Value.Summary.InvoiceCount);
        Assert.Equal(0, result.Value.Summary.CreditNoteCount);
    }

    [Fact]
    public async Task A_document_returned_twice_by_sap_is_counted_once()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var invoice = Invoice(1, 5008, "CIS006", "2026-07-04", ("YOG143", 100m, 250m));

        var handler = CreateHandler(invoices: [invoice, invoice], creditNotes: []);

        var result = await handler.Handle(Query(), default);

        Assert.Equal(100m, Assert.Single(result.Value.ItemTotals).NetQuantity);
        Assert.Equal(1, result.Value.Summary.InvoiceCount);
    }

    [Fact]
    public async Task Zig_and_usd_are_reported_side_by_side_rather_than_added_together()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var zigInvoice = Invoice(2, 5010, "CIS006", "2026-07-06", ("YOG143", 50m, 6500m));
        zigInvoice.DocCurrency = "ZIG";

        var handler = CreateHandler(
            invoices: [Invoice(1, 5009, "CIS006", "2026-07-04", ("YOG143", 100m, 250m)), zigInvoice],
            creditNotes: []);

        var result = await handler.Handle(Query(), default);

        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal(250m, item.NetRevenueUsd);
        Assert.Equal(6500m, item.NetRevenueZig);
        // The quantity is a quantity in either currency, so it does combine.
        Assert.Equal(150m, item.NetQuantity);
        Assert.Equal(90m, item.NetVolume);
    }

    [Fact]
    public async Task Account_code_ranges_are_expanded_before_sap_is_asked()
    {
        var requested = new List<IReadOnlyList<string>>();
        var handler = CreateHandler(invoices: [], creditNotes: [], invoiceCallLog: requested);

        await handler.Handle(Query(accountCodes: ["VAN008-010", "CIS006"]), default);

        Assert.Equal(["VAN008", "VAN009", "VAN010", "CIS006"], Assert.Single(requested));
    }

    [Fact]
    public async Task Item_codes_are_matched_regardless_of_the_case_sap_returns_them_in()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5011, "CIS006", "2026-07-04", ("yog143", 100m, 250m))],
            creditNotes: []);

        var result = await handler.Handle(Query(itemCodes: ["yog143"]), default);

        var item = Assert.Single(result.Value.ItemTotals);
        Assert.Equal("YOG143", item.ItemCode);
        Assert.Equal(60m, item.NetVolume);
    }

    [Fact]
    public async Task Periods_carry_the_same_netting_as_the_totals()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5012, "CIS006", "2026-07-04", ("YOG143", 100m, 250m))],
            creditNotes: [CreditNote(9, 7012, "CIS006", "2026-07-04", ("YOG143", 10m, 25m))]);

        var result = await handler.Handle(Query(grouping: ItemVolumeSalesGrouping.Daily), default);

        var day = result.Value.Periods.Single(period => period.PeriodStartUtc == new DateTime(2026, 7, 4));
        Assert.Equal(90m, day.NetQuantity);
        Assert.Equal(54m, day.NetVolume);
        Assert.Equal(225m, day.NetRevenueUsd);
        Assert.Equal(1, day.InvoiceCount);
        Assert.Equal(1, day.CreditNoteCount);
    }

    [Fact]
    public async Task Document_lines_state_a_credit_negatively_so_the_detail_reconciles_to_the_total()
    {
        await SeedFactorAsync("YOG143", 0.6m);

        var handler = CreateHandler(
            invoices: [Invoice(1, 5013, "CIS006", "2026-07-04", ("YOG143", 100m, 250m))],
            creditNotes: [CreditNote(9, 7013, "CIS006", "2026-07-18", ("YOG143", 10m, 25m))]);

        var result = await handler.Handle(Query(), default);

        var credit = result.Value.DocumentLines.Single(line => line.DocumentType == "Credit Note");
        Assert.Equal(-10m, credit.Quantity);
        Assert.Equal(-25m, credit.LineAmount);
        Assert.Equal(-6m, credit.Volume);

        Assert.Equal(
            result.Value.Summary.NetQuantity,
            result.Value.DocumentLines.Sum(line => line.Quantity));
    }

    private async Task SeedFactorAsync(string itemCode, decimal factor, bool isActive = true)
    {
        _context.ItemVolumeConversions.Add(new ItemVolumeConversionEntity
        {
            ItemCode = itemCode,
            VolumeFactor = factor,
            IsActive = isActive
        });

        await _context.SaveChangesAsync();
    }

    private static GetItemVolumeSalesReportQuery Query(
        IReadOnlyList<string>? accountCodes = null,
        IReadOnlyList<string>? itemCodes = null,
        ItemVolumeSalesGrouping grouping = ItemVolumeSalesGrouping.Monthly) =>
        new(
            DateTime.Parse(From),
            DateTime.Parse(To),
            grouping,
            accountCodes ?? ["CIS006"],
            itemCodes ?? []);

    private GetItemVolumeSalesReportHandler CreateHandler(
        List<Invoice> invoices,
        List<SAPCreditNote> creditNotes,
        List<IReadOnlyList<string>>? invoiceCallLog = null)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoicesByCustomersAsync) => LogAndReturn(args, invoices),
            nameof(ISAPServiceLayerClient.GetCreditNotesByCustomersAsync) => Task.FromResult(creditNotes),
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected — the report reads invoices and credit notes only.")
        });

        return new GetItemVolumeSalesReportHandler(
            _context,
            sap,
            NullLogger<GetItemVolumeSalesReportHandler>.Instance);

        object LogAndReturn(object?[]? args, List<Invoice> result)
        {
            invoiceCallLog?.Add(((IEnumerable<string>)args![0]!).ToList());
            return Task.FromResult(result);
        }
    }

    private static Invoice Invoice(
        int docEntry,
        int docNum,
        string cardCode,
        string docDate,
        params (string ItemCode, decimal Quantity, decimal LineTotal)[] lines) =>
        new()
        {
            DocEntry = docEntry,
            DocNum = docNum,
            CardCode = cardCode,
            CardName = $"{cardCode} Trading",
            DocDate = docDate,
            DocCurrency = "USD",
            Cancelled = "tNO",
            DocTotal = lines.Sum(line => line.LineTotal),
            DocumentLines = lines
                .Select((line, index) => new InvoiceLine
                {
                    LineNum = index,
                    ItemCode = line.ItemCode,
                    ItemDescription = $"{line.ItemCode} description",
                    Quantity = line.Quantity,
                    LineTotal = line.LineTotal
                })
                .ToList()
        };

    private static SAPCreditNote CreditNote(
        int docEntry,
        int docNum,
        string cardCode,
        string docDate,
        params (string ItemCode, decimal Quantity, decimal LineTotal)[] lines) =>
        new()
        {
            DocEntry = docEntry,
            DocNum = docNum,
            CardCode = cardCode,
            CardName = $"{cardCode} Trading",
            DocDate = docDate,
            DocCurrency = "USD",
            Cancelled = "tNO",
            DocTotal = lines.Sum(line => line.LineTotal),
            DocumentLines = lines
                .Select((line, index) => new SAPCreditNoteLine
                {
                    LineNum = index,
                    ItemCode = line.ItemCode,
                    ItemDescription = $"{line.ItemCode} description",
                    Quantity = line.Quantity,
                    LineTotal = line.LineTotal
                })
                .ToList()
        };
}
