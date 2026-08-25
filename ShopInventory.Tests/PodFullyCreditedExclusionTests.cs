using ClosedXML.Excel;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Web.Common;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A credit note that fully reverses an invoice means the delivery did not stand. There is no
/// proof of delivery to chase and there never will be, so the POD report holds those invoices
/// apart: off its own list, out of its counts, out of the completion percentage, and into a
/// workbook of their own.
///
/// These hold both halves of that -- that the report stops counting them, and that nothing is
/// silently lost by the exclusion.
/// </summary>
public sealed class PodFullyCreditedExclusionTests
{
    [Fact]
    public void A_fully_credited_invoice_leaves_the_report_and_its_counts()
    {
        var report = new PodUploadStatusReportDto
        {
            Items =
            [
                ApiItem(docNum: 5001, hasPod: true),
                ApiItem(docNum: 5002, hasPod: false),
                ApiItem(docNum: 5003, hasPod: false, isFullyCredited: true)
            ]
        };

        GetPodUploadStatusHandler.ApplyFullyCreditedSplit(report);

        Assert.Equal([5001, 5002], report.Items.Select(item => item.DocNum));
        Assert.Equal(2, report.TotalInvoices);
        Assert.Equal(1, report.UploadedCount);
        Assert.Equal(1, report.PendingCount);

        // Not dropped -- moved.
        Assert.Equal(5003, Assert.Single(report.FullyCreditedItems).DocNum);
        Assert.Equal(1, report.FullyCreditedCount);
    }

    /// <summary>
    /// The completion figure is the thing the exclusion is for. One uploaded of three invoices
    /// is 33%; the same day with the un-chaseable invoice taken out is 50%, and that is the
    /// figure that describes work anybody can actually do.
    /// </summary>
    [Fact]
    public void Completion_is_stated_over_the_invoices_that_can_still_be_documented()
    {
        var report = new PodUploadStatusReportDto
        {
            Items =
            [
                ApiItem(docNum: 5001, hasPod: true),
                ApiItem(docNum: 5002, hasPod: false),
                ApiItem(docNum: 5003, hasPod: false, isFullyCredited: true)
            ]
        };

        GetPodUploadStatusHandler.ApplyFullyCreditedSplit(report);

        Assert.Equal(50, report.UploadedCount * 100 / report.TotalInvoices);
    }

    /// <summary>
    /// The split reads both lists, so a cached snapshot re-partitions on the credit-note status
    /// as it now stands. That covers a snapshot written before the split existed (credited
    /// invoices still on Items) and a credit note since cancelled, which puts its invoice back
    /// on the chase list rather than stranding it on the credited one.
    /// </summary>
    [Fact]
    public void A_cached_report_repartitions_on_the_status_as_it_now_stands()
    {
        var report = new PodUploadStatusReportDto
        {
            // As a pre-split snapshot deserialises: credited invoice still on the main list.
            Items = [ApiItem(docNum: 5001, hasPod: false, isFullyCredited: true)],
            // And one whose credit note has since been cancelled, so the flag is now false.
            FullyCreditedItems = [ApiItem(docNum: 5002, hasPod: true)]
        };

        GetPodUploadStatusHandler.ApplyFullyCreditedSplit(report);

        Assert.Equal(5002, Assert.Single(report.Items).DocNum);
        Assert.Equal(5001, Assert.Single(report.FullyCreditedItems).DocNum);
        Assert.Equal(1, report.TotalInvoices);
        Assert.Equal(1, report.UploadedCount);
        Assert.Equal(1, report.FullyCreditedCount);
    }

    /// <summary>
    /// Both workbooks are drawn from the same reporting scope. An excluded creator or business
    /// partner has to fall out of both, or the two disagree about what the period held.
    /// </summary>
    [Fact]
    public void The_reporting_scope_drops_the_same_invoices_from_both_lists()
    {
        var report = new PodUploadStatusReport
        {
            Items =
            [
                WebItem(docNum: 5001),
                // VAN001 is an excluded business partner.
                WebItem(docNum: 5002, cardCode: "VAN001")
            ],
            FullyCreditedItems =
            [
                WebItem(docNum: 6001, isFullyCredited: true),
                WebItem(docNum: 6002, cardCode: "VAN001", isFullyCredited: true),
                // No generated location: unmapped creators are out of scope on both lists.
                new PodUploadStatusItem
                {
                    DocEntry = 6003, DocNum = 6003, DocDate = "2026-08-06",
                    CardCode = "ABS006", CardName = "Absolute Refregiration",
                    DocTotal = 40m, DocCurrency = "USD", IsFullyCredited = true,
                    CreditNoteNumber = "9003"
                }
            ]
        };

        var scoped = ReportExportService.ApplyPodReportingScope(report);

        Assert.Equal([5001], scoped.Items.Select(item => item.DocNum));
        Assert.Equal([6001], scoped.FullyCreditedItems.Select(item => item.DocNum));
        Assert.Equal(1, scoped.FullyCreditedCount);
        Assert.Equal(1, scoped.TotalInvoices);
    }

    [Fact]
    public void The_pod_workbook_no_longer_lists_a_fully_credited_invoice()
    {
        var bytes = new ReportExportService().ExportPodUploadStatusToExcel(
            BuildReport(),
            DeliveryRouteDirectory.Build([]));

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        foreach (var sheetName in new[] { "Product Invoices", "Pending PODs" })
        {
            var invoiceNumbers = ReadInvoiceNumbers(workbook.Worksheet(sheetName));
            Assert.Contains("5002", invoiceNumbers);
            Assert.DoesNotContain("6001", invoiceNumbers);
        }

        // The completion KPI is over the two chaseable invoices, one of which is uploaded.
        Assert.Contains("50.0%", ReadSheetText(workbook.Worksheet("Product Invoices")));
    }

    [Fact]
    public void The_credited_workbook_lists_the_invoices_the_pod_report_left_out()
    {
        var bytes = new ReportExportService().ExportPodFullyCreditedInvoicesToExcel(
            BuildReport(),
            DeliveryRouteDirectory.Build([]));

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        Assert.Equal(
            ["Fully Credited", "By Customer", "By Reason"],
            workbook.Worksheets.Select(sheet => sheet.Name));

        var detail = workbook.Worksheet("Fully Credited");
        var headerRow = FindHeaderRow(detail, "Invoice #");
        Assert.Equal("Credit Note #", detail.Cell(headerRow, 8).GetString());
        Assert.Equal("Credit Reason", detail.Cell(headerRow, 9).GetString());

        var invoiceNumbers = ReadInvoiceNumbers(detail);
        Assert.Equal(["6001", "6002"], invoiceNumbers.Order());
        Assert.DoesNotContain("5001", invoiceNumbers);

        // The row keeps the detail a reviewer opens this workbook for.
        var creditedRow = FindRowByFirstCell(detail, "6001");
        Assert.Equal("9001", detail.Cell(creditedRow, 8).GetString());
        Assert.Equal("Returned - short delivery", detail.Cell(creditedRow, 9).GetString());
        Assert.Equal("USD", detail.Cell(creditedRow, 7).GetString());

        // Per currency, never one sum across them.
        var detailText = ReadSheetText(detail);
        Assert.Contains("USD 200.00", detailText);
        Assert.Contains("ZWG 90.00", detailText);

        // The figure the POD report's completion no longer carries, stated where it is missed.
        Assert.Contains("Still Chased", detailText);

        var byReason = ReadSheetText(workbook.Worksheet("By Reason"));
        Assert.Contains("Returned - short delivery", byReason);
        Assert.Contains("No reason supplied", byReason);
    }

    /// <summary>
    /// A clean period still produces the workbook. An empty grid between a header and a totals
    /// row reads as a report that failed to run rather than as a period with nothing reversed.
    /// </summary>
    [Fact]
    public void A_period_with_nothing_credited_still_produces_a_readable_workbook()
    {
        var report = BuildReport();
        report.FullyCreditedItems.Clear();
        report.FullyCreditedCount = 0;

        var bytes = new ReportExportService().ExportPodFullyCreditedInvoicesToExcel(
            report,
            DeliveryRouteDirectory.Build([]));

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        foreach (var sheet in workbook.Worksheets)
        {
            Assert.Contains("No invoice in this period was fully credited.", ReadSheetText(sheet));
        }
    }

    private static PodUploadStatusReport BuildReport() => new()
    {
        FromDate = "2026-08-01",
        ToDate = "2026-08-20",
        TotalInvoices = 2,
        UploadedCount = 1,
        PendingCount = 1,
        FullyCreditedCount = 2,
        CreditNoteDataComplete = true,
        Items =
        [
            WebItem(docNum: 5001, hasPod: true),
            WebItem(docNum: 5002)
        ],
        FullyCreditedItems =
        [
            WebItem(
                docNum: 6001,
                isFullyCredited: true,
                creditNoteNumber: "9001",
                creditNoteReason: "Returned - short delivery",
                docTotal: 200m),
            WebItem(
                docNum: 6002,
                isFullyCredited: true,
                creditNoteNumber: "9002",
                docTotal: 90m,
                currency: "ZWG")
        ]
    };

    private static PodUploadStatusItemDto ApiItem(
        int docNum,
        bool hasPod,
        bool isFullyCredited = false) => new()
        {
            DocEntry = docNum,
            DocNum = docNum,
            DocDate = "2026-08-04",
            CardCode = "SPA059 USD",
            CardName = "SPAR Athienitis",
            DocTotal = 100m,
            DocCurrency = "USD",
            HasPod = hasPod,
            HasProductPod = hasPod,
            ProductPodCount = hasPod ? 1 : 0,
            PodCount = hasPod ? 1 : 0,
            IsFullyCredited = isFullyCredited
        };

    // CreatedLocation is required: ApplyPodReportingScope drops an invoice with no generated
    // location before either workbook is built.
    private static PodUploadStatusItem WebItem(
        int docNum,
        bool hasPod = false,
        bool isFullyCredited = false,
        string cardCode = "SPA059 USD",
        string? creditNoteNumber = null,
        string? creditNoteReason = null,
        decimal docTotal = 100m,
        string currency = "USD") => new()
        {
            DocEntry = docNum,
            DocNum = docNum,
            DocDate = "2026-08-04",
            CardCode = cardCode,
            CardName = "SPAR Athienitis",
            DocTotal = docTotal,
            DocCurrency = currency,
            CreatedLocation = "Cheeseman",
            HasPod = hasPod,
            HasProductPod = hasPod,
            ProductPodCount = hasPod ? 1 : 0,
            PodCount = hasPod ? 1 : 0,
            PodUploadedAt = hasPod ? new DateTime(2026, 8, 5, 9, 30, 0, DateTimeKind.Utc) : null,
            IsFullyCredited = isFullyCredited,
            CreditNoteNumber = creditNoteNumber,
            CreditNoteReason = creditNoteReason
        };

    private static List<string> ReadInvoiceNumbers(IXLWorksheet sheet)
    {
        var headerRow = FindHeaderRow(sheet, "Invoice #");
        var lastRow = sheet.LastRowUsed()!.RowNumber();
        var numbers = new List<string>();

        for (var row = headerRow + 1; row <= lastRow; row++)
        {
            var value = sheet.Cell(row, 1).GetString();
            if (value is "TOTAL" or "SUMMARY")
                break;

            if (!string.IsNullOrWhiteSpace(value))
                numbers.Add(value);
        }

        return numbers;
    }

    private static int FindRowByFirstCell(IXLWorksheet sheet, string value)
    {
        for (var row = 1; row <= sheet.LastRowUsed()!.RowNumber(); row++)
        {
            if (sheet.Cell(row, 1).GetString() == value)
                return row;
        }

        throw new Xunit.Sdk.XunitException($"no row starting {value} on {sheet.Name}");
    }

    private static int FindHeaderRow(IXLWorksheet sheet, string firstHeader)
    {
        for (var row = 1; row <= sheet.LastRowUsed()!.RowNumber(); row++)
        {
            if (sheet.Cell(row, 1).GetString() == firstHeader)
                return row;
        }

        throw new Xunit.Sdk.XunitException($"no header row on {sheet.Name}");
    }

    private static string ReadSheetText(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));
}
