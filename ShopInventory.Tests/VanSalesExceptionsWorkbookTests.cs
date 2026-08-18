using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opens the exceptions workbook and reads its cells back.
///
/// The disclosures matter more here than anywhere else in this suite. This is the report whose whole
/// subject is money the other van reports cannot see, and a workbook is the copy that travels: whoever
/// opens it second never saw the page that explained why an expired document is a sale that was served,
/// or that a held queue is not draining because nothing is trying to drain it. If a sentence does not
/// survive the export it is not being said.
///
/// Two of these tests exist for a specific misreading. A stopped posting job looks exactly like a slow
/// one on the "Held" tab, so the switch has to be named on that sheet and not only on the overview. And
/// a rate with no denominator has to read as unavailable, because a 0% written where a share is unknown
/// is the report claiming perfect capture.
/// </summary>
public class VanSalesExceptionsWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    /// <summary>Everything in the file, for the disclosures that may live on more than one sheet.</summary>
    private static string TextOf(XLWorkbook workbook) =>
        string.Join("\n", workbook.Worksheets.Select(TextOf));

    /// <summary>The first data cell under a column heading. Every table here writes its rows in order.</summary>
    private static IXLCell UnderHeader(IXLWorksheet sheet, string header)
    {
        var address = sheet.CellsUsed().First(cell => cell.GetString() == header).Address;
        return sheet.Cell(address.RowNumber + 1, address.ColumnNumber);
    }

    /// <summary>The KPI strip writes a value above its label, so a KPI is found by its label.</summary>
    private static string KpiValue(IXLWorksheet sheet, string label)
    {
        var address = sheet.CellsUsed().First(cell => cell.GetString() == label).Address;
        return sheet.Cell(address.RowNumber - 1, address.ColumnNumber).GetFormattedString();
    }

    // ── Shape ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_workbook_carries_a_sheet_for_every_section()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));

        Assert.Equal(
            ["Overview", "Unseen", "Settlement", "Held", "Receipts", "Hygiene"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    /// <summary>The case that reaches production first.</summary>
    [Fact]
    public void An_empty_period_still_produces_a_readable_workbook()
    {
        var bytes = _service.ExportVanSalesExceptionsToExcel(new VanSalesExceptionsReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31)
        });

        using var workbook = Open(bytes);
        var overview = workbook.Worksheet("Overview");

        Assert.Equal(6, workbook.Worksheets.Count);
        Assert.Contains("VAN SALES EXCEPTIONS", TextOf(overview));

        // A period with nothing in it still cannot compare declared cash against banked cash, and the
        // workbook says so. There is no clean period for this report, and an empty caveat block would
        // itself be a claim.
        Assert.Contains("Declared cash is not compared here", TextOf(overview));

        // No sales means no untendered share and no unseen share. Zero would read as perfect capture.
        Assert.Equal("—", KpiValue(overview, "Untendered Share"));
        Assert.Equal("—", KpiValue(overview, "Unseen Share"));
        Assert.Equal("—", KpiValue(overview, "Takings"));
    }

    // ── What the period could not answer ────────────────────────────────────────

    /// <summary>
    /// Every caveat the period carries, written out in full. The list is walked from the report itself
    /// rather than hard-coded, so a caveat added later is covered the day it is added — and the count
    /// is asserted so the fixture cannot quietly stop exercising a branch.
    /// </summary>
    [Fact]
    public void Every_caveat_the_period_carries_survives_the_export()
    {
        var report = Report();

        // One handover status throughout is what a handset build predating the signed-receipt upload
        // looks like, and it is the last branch the fixture does not otherwise reach.
        report.ReceiptHandover = [report.ReceiptHandover[0]];
        report.Quality.ReceiptStatusesSeen = 1;

        var caveats = report.Quality.Caveats.ToList();
        Assert.Equal(10, caveats.Count);

        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(report));
        var text = TextOf(workbook);

        foreach (var caveat in caveats)
        {
            Assert.Contains(caveat, text);
        }
    }

    /// <summary>
    /// The switch is the difference between a queue and a failure, so it is named ahead of the figures
    /// it governs and again on the sheet where those figures are read. A reader who opened the workbook
    /// at the "Held" tab has seen no banner at all.
    /// </summary>
    [Fact]
    public void A_switched_off_posting_job_is_named_on_the_overview_and_again_on_the_held_sheet()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));

        Assert.Contains("POSTING JOB IS SWITCHED OFF", TextOf(workbook.Worksheet("Overview")));
        Assert.Contains("POSTING JOB IS SWITCHED OFF", TextOf(workbook.Worksheet("Held")));

        // And what it means for the ages beside it.
        Assert.Contains("has been waiting rather than", TextOf(workbook.Worksheet("Held")));
    }

    /// <summary>
    /// A running job says nothing, so the banner's presence is itself the signal. Left in when the job
    /// is running it would train readers to skip it.
    /// </summary>
    [Fact]
    public void A_running_posting_job_carries_no_switch_banner()
    {
        var report = Report();
        report.Quality.PostingJobEnabled = true;

        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(report));

        Assert.DoesNotContain("switched off", TextOf(workbook), StringComparison.OrdinalIgnoreCase);
    }

    // ── The register itself ─────────────────────────────────────────────────────

    /// <summary>
    /// The single most misleading thing the suite does, stated on the face of the sheet that counts it.
    /// A reader who does not meet the mechanism reads "Expired" as a tidy-up.
    /// </summary>
    [Fact]
    public void The_unseen_sheet_states_the_outage_mechanism_in_full()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));
        var text = TextOf(workbook.Worksheet("Unseen"));

        Assert.Contains("expires it within the hour", text);
        Assert.Contains("confirmed reservations only", text);

        // The reports read better on the estate's worst day. Nothing else in the suite says this.
        Assert.Contains("which is to say better", text);

        // And the limit on the other side: this is not a second definition of a van sale.
        Assert.Contains("second definition of a van sale", text);
    }

    /// <summary>
    /// The state is translated on the row. "Expired" is a database word and it costs money; the column
    /// says what it cost.
    /// </summary>
    [Fact]
    public void An_expired_document_is_named_on_its_row_as_a_sale_that_was_served()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));
        var sheet = workbook.Worksheet("Unseen");

        Assert.Equal("Expired", UnderHeader(sheet, "State").GetString());
        Assert.Equal(
            "sale made and served; no van report counts it",
            UnderHeader(sheet, "What It Means").GetString());
    }

    /// <summary>A document nobody can be traced to is named, not dropped and not blamed on anyone.</summary>
    [Fact]
    public void A_document_with_no_rep_behind_it_reads_as_unattributed()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));

        Assert.Contains("Unattributed", TextOf(workbook.Worksheet("Unseen")));
    }

    // ── Nulls ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing has attempted these sales, so there is no oldest attempt and no error. An em dash, never
    /// a zero, which would read as a sale that posted the moment it arrived.
    /// </summary>
    [Fact]
    public void A_held_row_with_nothing_behind_it_reads_as_an_em_dash_rather_than_zero()
    {
        var report = Report();
        report.Held[0].OldestDocDate = null;
        report.Held[0].OldestAgeDays = null;

        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(report));
        var sheet = workbook.Worksheet("Held");

        Assert.Equal("—", UnderHeader(sheet, "Oldest Sale").GetFormattedString());
        Assert.Equal("—", UnderHeader(sheet, "Days Waiting").GetFormattedString());

        // No error, because nothing has tried. Not the same as an error count of none.
        Assert.Equal("—", UnderHeader(sheet, "Last Error").GetFormattedString());
    }

    /// <summary>
    /// A share with no takings under it has no value, and 0% would read as a rep who banked no cash at
    /// all. The money itself is a real zero and prints as one — only the share is unavailable.
    /// </summary>
    [Fact]
    public void A_rate_with_no_takings_behind_it_reads_as_an_em_dash_rather_than_zero_percent()
    {
        var report = Report();
        report.TenderByRep[0].Gross = 0m;
        report.TenderByRep[0].CashGross = 0m;
        report.TenderByRep[0].UntenderedGross = 0m;

        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(report));
        var sheet = workbook.Worksheet("Settlement");

        Assert.Equal("—", UnderHeader(sheet, "Cash Share").GetFormattedString());
        Assert.Equal("—", UnderHeader(sheet, "Untendered Share").GetFormattedString());
        Assert.Equal("USD 0.00", UnderHeader(sheet, "Takings").GetString());
    }

    /// <summary>
    /// A tender row that settled nothing has no average document value. The record guards the division
    /// and the workbook has to honour the guard rather than printing the zero it avoided.
    /// </summary>
    [Fact]
    public void A_tender_row_with_no_documents_has_no_average_document_value()
    {
        var report = Report();
        report.Tender[0].DocumentCount = 0;
        report.Tender[0].Gross = 0m;

        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(report));
        var sheet = workbook.Worksheet("Settlement");

        Assert.Equal("—", UnderHeader(sheet, "Average Document").GetFormattedString());
    }

    // ── Money ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two currencies are two strings in one cell, never one number. Text, so a reader who selects the
    /// column gets nothing to add rather than a total that means nothing.
    /// </summary>
    [Fact]
    public void Two_currencies_are_written_as_two_strings_and_never_as_one_total()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));

        var takings = UnderHeader(workbook.Worksheet("Overview"), "Value");
        Assert.Equal(XLDataType.Text, takings.DataType);
        Assert.Contains("USD 9,120.00", takings.GetString());
        Assert.Contains("ZWG 41,300.00", takings.GetString());

        var unseen = TextOf(workbook.Worksheet("Unseen"));
        Assert.Contains("USD 1,234.50", unseen);
        Assert.Contains("ZWG 900.00", unseen);

        var everything = TextOf(workbook);
        Assert.DoesNotContain("50,420", everything);   // the takings, summed across currencies
        Assert.DoesNotContain("2,134.50", everything); // the unseen exposure, summed across currencies
    }

    /// <summary>
    /// Settled money, unseen money and held money are three populations, and the workbook says out loud
    /// that it never adds them. Exposure is not takings: one is revenue and one is a document that may
    /// never have been a sale.
    /// </summary>
    [Fact]
    public void The_three_populations_of_money_are_named_as_separate_and_never_totalled()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));
        var text = TextOf(workbook.Worksheet("Overview"));

        Assert.Contains("NO ARITHMETIC ADDS THEM", text);
        Assert.Contains("never totals them", text);
        Assert.Contains("Settled sales", text);
        Assert.Contains("Unseen documents", text);
        Assert.Contains("Held offline sales", text);
    }

    /// <summary>
    /// Untendered money is already inside both Takings and Other. Without that sentence the columns look
    /// like a set to add, and the sheet would appear to overstate the period by the untendered figure.
    /// </summary>
    [Fact]
    public void The_settlement_sheet_says_untendered_money_is_already_inside_the_totals()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));
        var text = TextOf(workbook.Worksheet("Settlement"));

        Assert.Contains("already sits inside Takings and inside Other", text);
        Assert.Contains("no method recorded", text);
    }

    // ── The other two disclosures ───────────────────────────────────────────────

    /// <summary>
    /// The value that means "nothing to submit" on a receipt that will never be submitted. A reader who
    /// does not meet it reads the distribution as evidence of a healthy fiscal path.
    /// </summary>
    [Fact]
    public void The_receipt_sheet_names_the_default_that_was_never_backfilled()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));
        var text = TextOf(workbook.Worksheet("Receipts"));

        Assert.Contains("NotApplicable deserves particular suspicion", text);
        Assert.Contains("never be submitted", text);

        // A sale with no device signature cannot be handed over whatever its status says.
        Assert.Contains("no device signature", text);
    }

    /// <summary>
    /// A line with a quantity and no value is a real zero — both capture paths permit it — so the sheet
    /// says so rather than leaving it to read as a rounding artefact. And a rep with nothing outstanding
    /// says so in words, because a row of zeroes reads as a rep with no sales.
    /// </summary>
    [Fact]
    public void The_hygiene_sheet_explains_a_real_zero_and_names_a_clean_rep()
    {
        using var workbook = Open(_service.ExportVanSalesExceptionsToExcel(Report()));
        var sheet = workbook.Worksheet("Hygiene");
        var text = TextOf(sheet);

        Assert.Contains("real zero", text);
        Assert.Contains("nothing outstanding", text);

        // Worst first, and the worst rep's outstanding count is the sum of its three failures.
        Assert.Equal("23", UnderHeader(sheet, "To Fix").GetString());
    }

    // ── A report with one of everything ─────────────────────────────────────────

    /// <summary>
    /// The estate as it actually stands: the posting job switched off, an outage's worth of expired
    /// documents, two currencies, a rep with no full name, an unattributed document and a clean rep.
    /// </summary>
    private static VanSalesExceptionsReportResponse Report() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        Summary = new VanSalesExceptionsSummary
        {
            SaleCount = 412,
            RepCount = 3,
            SalesWithoutTender = 18,
            SalesWithoutOutlet = 7,
            LinesWithoutValue = 4,
            UnseenDocumentCount = 31,
            ExpiredDocumentCount = 24,
            HeldSaleCount = 9,
            OldestHeldAgeDays = 12,
            UnseenExposure = [Exposure("USD", 24, 1234.50m), Exposure("ZWG", 7, 900m)],
            HeldExposure = [Exposure("USD", 9, 420m)],
            TotalsByCurrency = [Money("USD", 300, 260, 9120m), Money("ZWG", 112, 96, 41300m)]
        },
        Tender =
        [
            Tender("USD", "Cash", false, 210, 6300m),
            Tender("USD", "Ecocash", false, 78, 2220m),
            Tender("USD", "Other", true, 12, 600m),
            Tender("ZWG", "Cash", false, 96, 38400m),
            Tender("ZWG", "Innbucks", false, 10, 2300m),
            Tender("ZWG", "Other", true, 6, 600m)
        ],
        TenderByRep =
        [
            new VanSalesRepTender
            {
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                Currency = "USD",
                DocumentCount = 180,
                Gross = 5400m,
                CashGross = 4200m,
                EcocashGross = 900m,
                InnbucksGross = 0m,
                OtherGross = 300m,
                UntenderedGross = 300m,
                UntenderedCount = 6
            },
            // No full name on the account, so the workbook falls back to the username rather than
            // printing a blank where a person should be.
            new VanSalesRepTender
            {
                UserId = Guid.NewGuid(),
                Username = "van011",
                FullName = null,
                Currency = "USD",
                DocumentCount = 120,
                Gross = 3720m,
                CashGross = 2100m,
                EcocashGross = 1320m,
                InnbucksGross = 0m,
                OtherGross = 300m,
                UntenderedGross = 300m,
                UntenderedCount = 6
            },
            new VanSalesRepTender
            {
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                Currency = "ZWG",
                DocumentCount = 112,
                Gross = 41300m,
                CashGross = 38400m,
                EcocashGross = 0m,
                InnbucksGross = 2300m,
                OtherGross = 600m,
                UntenderedGross = 600m,
                UntenderedCount = 6
            }
        ],
        Unseen =
        [
            new VanSalesUnseen
            {
                Status = "Expired",
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                DocumentCount = 24,
                EarliestCapturedAt = new DateTime(2026, 8, 12, 7, 15, 0),
                LatestCapturedAt = new DateTime(2026, 8, 12, 14, 40, 0),
                Exposure = [Exposure("USD", 24, 1234.50m)]
            },
            // Nothing on the reservation names a rep, so it is reported as unattributed rather than
            // guessed at.
            new VanSalesUnseen
            {
                Status = "Pending",
                UserId = null,
                Username = null,
                FullName = null,
                DocumentCount = 7,
                EarliestCapturedAt = new DateTime(2026, 8, 30, 16, 5, 0),
                LatestCapturedAt = new DateTime(2026, 8, 31, 9, 20, 0),
                Exposure = [Exposure("ZWG", 7, 900m)]
            }
        ],
        Held =
        [
            new VanSalesHeld
            {
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                SaleCount = 6,
                OldestDocDate = new DateTime(2026, 8, 19),
                OldestAgeDays = 12,
                AttemptedCount = 0,
                FailedCount = 0,
                LastError = null,
                Exposure = [Exposure("USD", 6, 300m)]
            },
            new VanSalesHeld
            {
                UserId = Guid.NewGuid(),
                Username = "van011",
                FullName = null,
                SaleCount = 3,
                OldestDocDate = new DateTime(2026, 8, 27),
                OldestAgeDays = 4,
                AttemptedCount = 0,
                FailedCount = 0,
                LastError = null,
                Exposure = [Exposure("USD", 3, 120m)]
            }
        ],
        ReceiptHandover =
        [
            new VanSalesReceiptHandover
            {
                Status = "NotApplicable",
                SaleCount = 400,
                WithSignature = 388,
                EarliestDocDate = new DateTime(2026, 8, 1),
                LatestDocDate = new DateTime(2026, 8, 31)
            },
            new VanSalesReceiptHandover
            {
                Status = "Submitted",
                SaleCount = 12,
                WithSignature = 12,
                EarliestDocDate = new DateTime(2026, 8, 4),
                LatestDocDate = new DateTime(2026, 8, 29)
            }
        ],
        Hygiene =
        [
            new VanSalesHygiene
            {
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                SaleCount = 250,
                WithoutTender = 12,
                WithoutOutlet = 7,
                LineCount = 1000,
                LinesWithoutValue = 4
            },
            new VanSalesHygiene
            {
                UserId = Guid.NewGuid(),
                Username = "van011",
                FullName = null,
                SaleCount = 120,
                WithoutTender = 6,
                WithoutOutlet = 0,
                LineCount = 460,
                LinesWithoutValue = 0
            },
            // Nothing outstanding. The sheet is a worklist, so this row exists to be read past.
            new VanSalesHygiene
            {
                UserId = Guid.NewGuid(),
                Username = "van012",
                FullName = "Farai Ncube",
                SaleCount = 42,
                WithoutTender = 0,
                WithoutOutlet = 0,
                LineCount = 160,
                LinesWithoutValue = 0
            }
        ],
        Quality = new VanSalesExceptionsQuality
        {
            UnseenDocumentCount = 31,
            ExpiredDocumentCount = 24,
            HeldSaleCount = 9,
            HeldNeverAttemptedCount = 9,
            SalesWithoutTender = 18,
            SalesWithoutOutlet = 7,
            LinesWithoutValue = 4,
            ReceiptStatusesSeen = 2,
            ReceiptsWithoutSignature = 12,
            PostingJobEnabled = false
        }
    };

    private static VanSalesMoney Money(string currency, int documents, int drops, decimal gross) =>
        new() { Currency = currency, DocumentCount = documents, DropCount = drops, Gross = gross };

    private static VanSalesExposure Exposure(string currency, int documents, decimal gross) =>
        new() { Currency = currency, DocumentCount = documents, Gross = gross };

    private static VanSalesTender Tender(
        string currency,
        string tender,
        bool untendered,
        int documents,
        decimal gross) =>
        new()
        {
            Currency = currency,
            TenderName = tender,
            Untendered = untendered,
            DocumentCount = documents,
            Gross = gross
        };
}
