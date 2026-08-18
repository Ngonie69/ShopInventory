using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesExceptionsReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the settlement split and the register of van documents the rest of the suite cannot see.
///
/// The lie these prevent is the one the whole report exists for: <b>a van report that reads better
/// the worse the day was</b>. A sale made while SAP is unreachable leaves a reservation that never
/// confirms, the cleanup job expires it within the hour, and the shared fact reader takes confirmed
/// documents only — so an outage removes money from every other page in the suite and removes it
/// silently, with no error, no empty result and no gap on a chart. The same shape recurs twice more:
/// an offline sale waits in a queue that nothing is draining, and a sale settled with no recorded
/// tender still adds to the takings while adding to no tender column.
///
/// Every case below is therefore written the same way — seed the document the suite drops, then
/// assert both that this report keeps it <em>and</em> that it is not quietly promoted into a sale.
/// The two populations have to stay disjoint: this file is the only place in the codebase that
/// reaches around <c>VanSalesFactReader</c>, and if it ever became a second definition of a van sale
/// the estate would have two answers to "what sold" and no way to tell which was right.
///
/// The caveat assertions are load-bearing rather than cosmetic. "Nothing has posted" and "nothing
/// could post" are the same number and opposite findings, and the only thing separating them on the
/// page is the state of the posting switch, which is read from configuration and stated in words.
/// </summary>
public sealed class VanSalesExceptionsReportTests : IDisposable
{
    private const string VanAccount = "VAN010";
    private const string VanWarehouse = "VAN010";

    private static readonly Guid Rep = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid OtherRep = Guid.Parse("99999999-9999-9999-9999-999999999999");

    /// <summary>The window every case is written around: the whole of August 2026.</summary>
    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesExceptionsReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        AddUser(Rep, "van010", "Tendai", "Moyo");

        // Deliberately nameless: the display name has to fall back to the username rather than to a
        // blank cell.
        AddUser(OtherRep, "van011", firstName: null, lastName: null);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The documents no other report sees ---

    /// <summary>
    /// The finding this report was built for. An invoice written while SAP was unreachable leaves a
    /// reservation that expires, and the fact reader takes confirmed documents only — so its money is
    /// in no van report at all, and the estate's figures improve during the outage.
    /// </summary>
    [Fact]
    public async Task An_expired_reservation_is_money_no_other_report_sees()
    {
        AddReservation("ON-LOST", ReservationStatus.Expired, Utc(new DateTime(2026, 8, 6), 9, 0), total: 120m);
        AddReservation("ON-SOLD", ReservationStatus.Confirmed, Utc(new DateTime(2026, 8, 6), 10, 0), total: 80m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        // The confirmed one is the sale, and it is the only sale.
        Assert.Equal(1, report.Summary.SaleCount);
        Assert.Equal(80m, Assert.Single(report.Summary.TotalsByCurrency).Gross);

        // The expired one is the whole point, and it is nowhere in the sale figures.
        var lost = Assert.Single(report.Unseen);
        Assert.Equal(ReservationStatus.Expired, lost.Status);
        Assert.True(lost.IsLostSale);
        Assert.Equal(1, lost.DocumentCount);
        Assert.Equal(120m, Assert.Single(lost.Exposure).Gross);
        Assert.Equal("Tendai Moyo", lost.DisplayName);

        Assert.Equal(1, report.Summary.UnseenDocumentCount);
        Assert.Equal(1, report.Summary.ExpiredDocumentCount);
        Assert.Equal(120m, Assert.Single(report.Summary.UnseenExposure).Gross);
        Assert.Equal(0.5, report.Summary.UnseenRate);

        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("expired without confirming"));
    }

    /// <summary>
    /// A confirmed reservation is a posted invoice and must never appear in the unseen register. If it
    /// did, this file would have become a second definition of a van sale and the two would disagree.
    /// </summary>
    [Fact]
    public async Task A_confirmed_reservation_is_never_in_the_unseen_register()
    {
        AddReservation("ON-SOLD", ReservationStatus.Confirmed, Utc(new DateTime(2026, 8, 6), 10, 0), total: 80m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Empty(report.Unseen);
        Assert.Equal(0, report.Summary.UnseenDocumentCount);
        Assert.Equal(1, report.Summary.SaleCount);
    }

    /// <summary>
    /// Cancelled, failed and pending are all documents the suite cannot see, but none of them is the
    /// outage signature: the sale was never served. Flagging them as lost would put money in the
    /// alarming column that nobody ever took.
    /// </summary>
    [Theory]
    [InlineData(ReservationStatus.Cancelled)]
    [InlineData(ReservationStatus.Failed)]
    [InlineData(ReservationStatus.Pending)]
    public async Task An_abandoned_document_is_unseen_without_being_a_lost_sale(string status)
    {
        AddReservation("ON-1", status, Utc(new DateTime(2026, 8, 6), 9, 0), total: 75m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var row = Assert.Single(report.Unseen);
        Assert.Equal(status, row.Status);
        Assert.False(row.IsLostSale);

        Assert.Equal(1, report.Summary.UnseenDocumentCount);
        Assert.Equal(0, report.Summary.ExpiredDocumentCount);
        Assert.Equal(0, report.Quality.ExpiredDocumentCount);

        Assert.DoesNotContain(report.Quality.Caveats, caveat => caveat.Contains("expired without confirming"));
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("pending, cancelled or failed"));
    }

    /// <summary>
    /// The unseen read deliberately drops the fact reader's <c>CreatedBy is not null</c> filter. The
    /// reader can afford to discard a document it cannot attribute because it has thousands of others;
    /// here the unattributable document <em>is</em> the finding, and discarding it would mean the one
    /// report that counts outage losses undercounting them.
    /// </summary>
    [Fact]
    public async Task An_unattributable_document_is_kept_and_reported_against_no_rep()
    {
        AddReservation(
            "ON-LOST",
            ReservationStatus.Expired,
            Utc(new DateTime(2026, 8, 6), 9, 0),
            total: 120m,
            createdBy: "system");

        // The same unparseable author on a confirmed document, to show what the reader does with it.
        AddReservation(
            "ON-SOLD",
            ReservationStatus.Confirmed,
            Utc(new DateTime(2026, 8, 6), 10, 0),
            total: 80m,
            createdBy: "system");

        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var row = Assert.Single(report.Unseen);
        Assert.Null(row.UserId);
        Assert.Null(row.Username);
        Assert.Equal("Unattributed", row.DisplayName);
        Assert.Equal(1, row.DocumentCount);
        Assert.Equal(120m, Assert.Single(row.Exposure).Gross);

        // And the contrast: the reader dropped its twin outright, so no report counts that one either.
        Assert.Equal(0, report.Summary.SaleCount);
    }

    /// <summary>
    /// The unseen and held reads go straight at the two tables rather than through the fact reader,
    /// which means they carry the source filter themselves. A till sale or a desktop reservation
    /// appearing here would be an exception raised against a van that never made it.
    /// </summary>
    [Fact]
    public async Task A_document_from_another_source_system_is_not_a_van_exception()
    {
        AddOfflineSale(
            "TILL-1",
            new DateTime(2026, 8, 6),
            total: 400m,
            sourceSystem: SaleSourceSystems.ShopTill);

        AddReservation(
            "DESK-1",
            ReservationStatus.Expired,
            Utc(new DateTime(2026, 8, 6), 9, 0),
            total: 900m,
            sourceSystem: SaleSourceSystems.LegacyDesktop);

        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Empty(report.Unseen);
        Assert.Empty(report.Held);
        Assert.Empty(report.ReceiptHandover);
        Assert.Equal(0, report.Summary.SaleCount);
        Assert.True(report.Quality.IsClean);
    }

    // --- The queue nothing is draining ---

    /// <summary>
    /// A held sale is one an offline van has uploaded and the posting job has not yet drained. Its age
    /// is measured from the oldest document to today rather than to the end of the window, because the
    /// question a reader has is how long the money has been waiting, not how long ago it was taken.
    ///
    /// A consolidated sale has reached SAP and is not held — counting it would turn the queue depth
    /// into a count of every van sale ever made.
    /// </summary>
    [Fact]
    public async Task A_held_sale_is_counted_with_its_age_and_a_consolidated_one_is_not()
    {
        AddOfflineSale("OFF-OLD", new DateTime(2026, 8, 5), total: 90m);
        AddOfflineSale("OFF-NEW", new DateTime(2026, 8, 12), total: 60m);
        AddOfflineSale(
            "OFF-POSTED",
            new DateTime(2026, 8, 6),
            total: 500m,
            consolidation: DesktopSaleConsolidationStatus.Consolidated);

        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var held = Assert.Single(report.Held);
        Assert.Equal(2, held.SaleCount);
        Assert.Equal(new DateTime(2026, 8, 5), held.OldestDocDate);
        Assert.Equal(150m, Assert.Single(held.Exposure).Gross);

        // Measured from today against the oldest of the two, not from the window's end and not
        // against the newer document.
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        Assert.Equal((int)(today - new DateTime(2026, 8, 5)).TotalDays, held.OldestAgeDays);
        Assert.Equal(held.OldestAgeDays, report.Summary.OldestHeldAgeDays);

        Assert.Equal(2, report.Summary.HeldSaleCount);
        Assert.Equal(150m, Assert.Single(report.Summary.HeldExposure).Gross);

        // All three are sales — the money was taken. Only two of them are still waiting to post.
        Assert.Equal(3, report.Summary.SaleCount);
    }

    /// <summary>
    /// The report's second headline. The posting job is switched off in this environment, so a pile of
    /// held sales is the size of a queue nothing is draining rather than a set of sales that failed —
    /// and the report has to say which, because the number is identical either way.
    /// </summary>
    [Fact]
    public async Task A_queue_no_posting_job_is_draining_is_stated_as_such()
    {
        AddOfflineSale("OFF-1", new DateTime(2026, 8, 5), total: 90m);
        AddOfflineSale("OFF-2", new DateTime(2026, 8, 6), total: 60m);
        await _context.SaveChangesAsync();

        var report = await RunAsync(postingEnabled: false);

        Assert.False(report.Quality.PostingJobEnabled);
        Assert.Equal(2, report.Quality.HeldNeverAttemptedCount);
        Assert.Equal(2, Assert.Single(report.Held).NeverAttemptedCount);

        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("posting job is switched off"));
        Assert.Contains(
            report.Quality.Caveats,
            caveat => caveat.Contains("Every held sale has a posting attempt count of zero"));
    }

    /// <summary>
    /// With the switch on, the same figures mean something else and the caveat must go — a standing
    /// warning that is always true teaches a reader to skip the whole block.
    /// </summary>
    [Fact]
    public async Task A_running_posting_job_raises_no_switch_caveat()
    {
        AddOfflineSale("OFF-1", new DateTime(2026, 8, 5), total: 90m);
        await _context.SaveChangesAsync();

        var report = await RunAsync(postingEnabled: true);

        Assert.True(report.Quality.PostingJobEnabled);
        Assert.DoesNotContain(report.Quality.Caveats, caveat => caveat.Contains("posting job is switched off"));
    }

    /// <summary>
    /// A sale a posting attempt has already touched is a failure rather than a queue, so the
    /// stopped-job caveat must not fire — the two need different people.
    /// </summary>
    [Fact]
    public async Task A_sale_a_posting_attempt_has_touched_is_a_failure_not_a_stopped_job()
    {
        AddOfflineSale(
            "OFF-1",
            new DateTime(2026, 8, 5),
            total: 90m,
            postingAttempts: 3,
            lastPostingError: "SAP returned -1029: item CHE011 is not defined in warehouse VAN010");

        await _context.SaveChangesAsync();

        var held = Assert.Single((await RunAsync()).Held);

        Assert.Equal(1, held.AttemptedCount);
        Assert.Equal(1, held.FailedCount);
        Assert.Equal(0, held.NeverAttemptedCount);
        Assert.Contains("-1029", held.LastError);

        Assert.DoesNotContain(
            (await RunAsync()).Quality.Caveats,
            caveat => caveat.Contains("Every held sale has a posting attempt count of zero"));
    }

    // --- Settlement ---

    /// <summary>
    /// The tender classifier puts a swipe and a sale that named no tender in the same bucket, which is
    /// right for a cash-up and wrong for a reader: one is a banking arrangement and the other is a
    /// capture failure. They must be two rows, or a route with no payment picker on its handsets reads
    /// as one taking card payments.
    /// </summary>
    [Fact]
    public async Task A_swipe_and_a_sale_naming_no_tender_are_never_the_same_row()
    {
        AddOfflineSale("OFF-SWIPE", new DateTime(2026, 8, 5), total: 40m, paymentMethod: "Swipe");
        AddOfflineSale("OFF-BLANK", new DateTime(2026, 8, 6), total: 100m, paymentMethod: null);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var other = report.Tender.Where(row => row.Tender == nameof(VanSalesTender.Other)).ToList();
        Assert.Equal(2, other.Count);

        var untendered = Assert.Single(other, row => row.Untendered);
        Assert.Equal(100m, untendered.Gross);
        Assert.Equal(1, untendered.DocumentCount);
        Assert.Equal(100m, untendered.AverageDocumentValue);

        var swipe = Assert.Single(other, row => !row.Untendered);
        Assert.Equal(40m, swipe.Gross);

        Assert.Equal(1, report.Summary.SalesWithoutTender);
        Assert.Equal(0.5, report.Summary.UntenderedRate);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("record no payment method"));
    }

    /// <summary>
    /// USD and ZiG are never added. A single scalar anywhere in this report would be a number in no
    /// currency at all, and it would look entirely plausible on the page.
    /// </summary>
    [Fact]
    public async Task Money_never_crosses_currencies()
    {
        AddOfflineSale("OFF-USD", new DateTime(2026, 8, 5), total: 100m, currency: "USD");
        AddOfflineSale("OFF-ZWG", new DateTime(2026, 8, 6), total: 900m, currency: "ZWG");

        AddReservation("ON-USD", ReservationStatus.Expired, Utc(new DateTime(2026, 8, 7), 9, 0), total: 50m, currency: "USD");
        AddReservation("ON-ZWG", ReservationStatus.Expired, Utc(new DateTime(2026, 8, 7), 10, 0), total: 700m, currency: "ZWG");

        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Tender.Count);
        Assert.Equal(100m, Assert.Single(report.Tender, row => row.Currency == "USD").Gross);
        Assert.Equal(900m, Assert.Single(report.Tender, row => row.Currency == "ZWG").Gross);

        Assert.Equal(2, report.Summary.TotalsByCurrency.Count);
        Assert.Equal(2, report.Summary.UnseenExposure.Count);
        Assert.Equal(50m, Assert.Single(report.Summary.UnseenExposure, row => row.Currency == "USD").Gross);
        Assert.Equal(700m, Assert.Single(report.Summary.UnseenExposure, row => row.Currency == "ZWG").Gross);

        // The one row a currency-blind roll-up would have produced.
        Assert.DoesNotContain(report.Summary.UnseenExposure, row => row.Gross == 750m);

        // One expired document per currency, and the counts are held apart too.
        Assert.All(report.Summary.UnseenExposure, row => Assert.Equal(1, row.DocumentCount));

        // A rep trading in two currencies is two rows, never one.
        Assert.Equal(2, report.TenderByRep.Count);
    }

    /// <summary>
    /// A rep's row has to add up: the four tender columns are a partition of that rep's takings, and
    /// the untendered figure cuts across them rather than being a fifth column. Getting that wrong
    /// double-counts a sale into both the cash split and the exception, or loses it from both.
    /// </summary>
    [Fact]
    public async Task A_reps_tender_columns_partition_their_takings()
    {
        AddOfflineSale("OFF-CASH", new DateTime(2026, 8, 5), total: 150m, paymentMethod: "Cash");
        AddOfflineSale("OFF-BLANK", new DateTime(2026, 8, 6), total: 50m, paymentMethod: null);
        await _context.SaveChangesAsync();

        var rep = Assert.Single((await RunAsync()).TenderByRep);

        Assert.Equal(Rep, rep.UserId);
        Assert.Equal("Tendai Moyo", rep.DisplayName);
        Assert.Equal(2, rep.DocumentCount);
        Assert.Equal(200m, rep.Gross);
        Assert.Equal(150m, rep.CashGross);

        Assert.Equal(
            rep.Gross,
            rep.CashGross + rep.EcocashGross + rep.InnbucksGross + rep.OtherGross);

        Assert.Equal(50m, rep.UntenderedGross);
        Assert.Equal(1, rep.UntenderedCount);
        Assert.Equal(0.75, rep.CashShare);
        Assert.Equal(0.25, rep.UntenderedShare);
    }

    // --- Capture hygiene ---

    /// <summary>
    /// Nothing in either capture path forbids a line that moved stock and charged nothing — the
    /// offline validator checks quantity and never price, and both check constraints are non-negative
    /// rather than positive. So a zero here is a free issue or a keying slip, not a rounding artefact,
    /// and it has to be counted rather than explained away.
    /// </summary>
    [Fact]
    public async Task A_line_carrying_a_quantity_and_no_value_is_counted()
    {
        AddOfflineSale("OFF-FREE", new DateTime(2026, 8, 5), total: 0m, quantity: 3m, lineTotal: 0m);
        AddOfflineSale("OFF-PAID", new DateTime(2026, 8, 6), total: 40m, quantity: 2m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.LinesWithoutValue);
        Assert.Equal(1, report.Quality.LinesWithoutValue);

        var rep = Assert.Single(report.Hygiene);
        Assert.Equal(2, rep.LineCount);
        Assert.Equal(1, rep.LinesWithoutValue);
        Assert.False(rep.IsClean);

        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("carry a quantity and no value"));
    }

    /// <summary>
    /// A line with no quantity at all moved nothing, so it is not a free issue and must not be counted
    /// as one. Only the online path can hold one — <c>DesktopSaleLines</c> has a positive-quantity
    /// check constraint and <c>StockReservationLines.OriginalQuantity</c> has none.
    /// </summary>
    [Fact]
    public async Task A_line_with_no_quantity_at_all_is_not_a_free_issue()
    {
        AddReservation(
            "ON-EMPTY",
            ReservationStatus.Confirmed,
            Utc(new DateTime(2026, 8, 5), 9, 0),
            total: 0m,
            originalQuantity: 0m,
            lineTotal: 0m);

        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(0, report.Summary.LinesWithoutValue);
        Assert.Equal(1, Assert.Single(report.Hygiene).LineCount);
        Assert.DoesNotContain(report.Quality.Caveats, caveat => caveat.Contains("carry a quantity and no value"));
    }

    /// <summary>
    /// The hygiene table is a worklist, so the rep with something outstanding comes before the one
    /// with nothing. A clean rep sorted to the top buries the row somebody has to act on.
    /// </summary>
    [Fact]
    public async Task The_hygiene_worklist_puts_the_rep_with_something_outstanding_first()
    {
        AddOfflineSale("OFF-CLEAN", new DateTime(2026, 8, 5), total: 40m, createdBy: OtherRep.ToString());
        AddOfflineSale("OFF-NOSHOP", new DateTime(2026, 8, 6), total: 60m, routeCustomerCode: null);
        AddOfflineSale("OFF-NOTENDER", new DateTime(2026, 8, 7), total: 30m, paymentMethod: null);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Hygiene.Count);

        var worst = report.Hygiene[0];
        Assert.Equal(Rep, worst.UserId);
        Assert.Equal(1, worst.WithoutOutlet);
        Assert.Equal(1, worst.WithoutTender);
        Assert.False(worst.IsClean);

        var clean = report.Hygiene[1];
        Assert.Equal(OtherRep, clean.UserId);
        // No first or last name on this user: the row falls back to the username, not to a blank.
        Assert.Equal("van011", clean.DisplayName);
        Assert.True(clean.IsClean);

        Assert.Equal(1, report.Summary.SalesWithoutOutlet);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("name no outlet"));
    }

    // --- The fiscal receipt handover ---

    /// <summary>
    /// A distribution rather than a count of exceptions, because every value in it is decided by
    /// fields a handset uploads and this repository cannot confirm which build the fleet is running.
    /// The signature count sits beside it: a sale that carries none can never be handed over whatever
    /// its status claims.
    /// </summary>
    [Fact]
    public async Task The_receipt_handover_section_returns_a_distribution()
    {
        AddOfflineSale(
            "OFF-1",
            new DateTime(2026, 8, 5),
            total: 40m,
            receiptStatus: DesktopSaleReceiptIngestStatus.Ingested);

        AddOfflineSale(
            "OFF-2",
            new DateTime(2026, 8, 6),
            total: 50m,
            receiptStatus: DesktopSaleReceiptIngestStatus.Ingested,
            deviceSignature: null);

        AddOfflineSale(
            "OFF-3",
            new DateTime(2026, 8, 7),
            total: 60m,
            receiptStatus: DesktopSaleReceiptIngestStatus.Pending);

        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.ReceiptHandover.Count);

        var ingested = Assert.Single(
            report.ReceiptHandover,
            row => row.Status == nameof(DesktopSaleReceiptIngestStatus.Ingested));

        Assert.Equal(2, ingested.SaleCount);
        Assert.Equal(1, ingested.WithSignature);
        Assert.Equal(1, ingested.WithoutSignature);
        Assert.Equal(new DateTime(2026, 8, 5), ingested.EarliestDocDate);
        Assert.Equal(new DateTime(2026, 8, 6), ingested.LatestDocDate);

        var pending = Assert.Single(
            report.ReceiptHandover,
            row => row.Status == nameof(DesktopSaleReceiptIngestStatus.Pending));

        Assert.Equal(1, pending.SaleCount);
        Assert.Equal(0, pending.WithoutSignature);

        Assert.Equal(2, report.Quality.ReceiptStatusesSeen);
        Assert.Equal(1, report.Quality.ReceiptsWithoutSignature);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("carry no device signature"));

        // Two statuses is a real spread, so the handover section stands on its own.
        Assert.DoesNotContain(
            report.Quality.Caveats,
            caveat => caveat.Contains("unestablished rather than as good news"));
    }

    /// <summary>
    /// One value across every sale in the period is what a handset build predating the signed-receipt
    /// upload looks like, and <c>NotApplicable</c> is the column's unbackfilled default. Reporting it
    /// as a clean handover would be reading a missing feature as a working one.
    /// </summary>
    [Fact]
    public async Task A_single_handover_status_is_read_as_unestablished_rather_than_as_good_news()
    {
        AddOfflineSale("OFF-1", new DateTime(2026, 8, 5), total: 40m);
        AddOfflineSale("OFF-2", new DateTime(2026, 8, 6), total: 50m);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var only = Assert.Single(report.ReceiptHandover);
        Assert.Equal(nameof(DesktopSaleReceiptIngestStatus.NotApplicable), only.Status);
        Assert.Equal(2, only.SaleCount);

        Assert.Equal(1, report.Quality.ReceiptStatusesSeen);
        Assert.Contains(
            report.Quality.Caveats,
            caveat => caveat.Contains("unestablished rather than as good news"));
    }

    // --- Nothing to report ---

    /// <summary>
    /// An empty period must read as a period with nothing in it, not as a perfect one and not as a
    /// stack trace. Every rate is null rather than zero, because a period with no sales has no
    /// untendered share and 0% would read as flawless capture.
    /// </summary>
    [Fact]
    public async Task An_empty_period_returns_a_readable_report_rather_than_throwing()
    {
        var report = await RunAsync();

        Assert.Equal(From, report.FromDate);
        Assert.Equal(To, report.ToDate);

        Assert.Equal(0, report.Summary.SaleCount);
        Assert.Equal(0, report.Summary.RepCount);
        Assert.Null(report.Summary.UntenderedRate);
        Assert.Null(report.Summary.UnseenRate);
        Assert.Null(report.Summary.OldestHeldAgeDays);

        Assert.Empty(report.Tender);
        Assert.Empty(report.TenderByRep);
        Assert.Empty(report.Unseen);
        Assert.Empty(report.Held);
        Assert.Empty(report.ReceiptHandover);
        Assert.Empty(report.Hygiene);
        Assert.Empty(report.Summary.TotalsByCurrency);
        Assert.Empty(report.Summary.UnseenExposure);
        Assert.Empty(report.Summary.HeldExposure);

        Assert.True(report.Quality.IsClean);
        Assert.NotEmpty(report.Quality.Caveats);
    }

    /// <summary>
    /// The report's standing limit, which is not conditional on anything and must never become so.
    /// Declared-versus-banked is the comparison that catches theft; nothing in this system records
    /// what was banked, so no page in this suite can offer it, and every page says so.
    /// </summary>
    [Fact]
    public async Task The_absence_of_a_banked_cash_comparison_is_stated_whatever_the_period_holds()
    {
        Assert.Contains((await RunAsync()).Quality.Caveats, caveat => caveat.Contains("Declared cash is not compared here"));

        AddOfflineSale("OFF-1", new DateTime(2026, 8, 5), total: 40m);
        await _context.SaveChangesAsync();

        Assert.Contains((await RunAsync()).Quality.Caveats, caveat => caveat.Contains("Declared cash is not compared here"));
    }

    // --- One rep ---

    /// <summary>
    /// Asking for one rep must scope the direct reads too. They sit outside the fact reader, so the
    /// filter it applies does not cover them and a leak here would put another van's expired invoices
    /// on this rep's page.
    /// </summary>
    [Fact]
    public async Task One_reps_report_holds_only_their_own_exceptions()
    {
        AddReservation("ON-MINE", ReservationStatus.Expired, Utc(new DateTime(2026, 8, 6), 9, 0), total: 120m);
        AddReservation(
            "ON-THEIRS",
            ReservationStatus.Expired,
            Utc(new DateTime(2026, 8, 6), 10, 0),
            total: 900m,
            createdBy: OtherRep.ToString());

        AddOfflineSale("OFF-MINE", new DateTime(2026, 8, 5), total: 40m);
        AddOfflineSale("OFF-THEIRS", new DateTime(2026, 8, 5), total: 700m, createdBy: OtherRep.ToString());

        await _context.SaveChangesAsync();

        var report = await RunAsync(userId: Rep);

        Assert.Equal(120m, Assert.Single(Assert.Single(report.Unseen).Exposure).Gross);
        Assert.Equal(40m, Assert.Single(Assert.Single(report.Held).Exposure).Gross);
        Assert.Equal(1, report.Summary.SaleCount);
    }

    // --- Validation ---

    /// <summary>A window that ends before it starts is a mis-typed filter, not an empty period.</summary>
    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var handler = new GetVanSalesExceptionsReportHandler(_context, Configuration(postingEnabled: false));

        var result = await handler.Handle(
            new GetVanSalesExceptionsReportQuery(To, From),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    /// <summary>A mistyped year must not turn one page load into a decade of history.</summary>
    [Fact]
    public async Task A_period_wider_than_the_suite_answers_for_is_refused()
    {
        var handler = new GetVanSalesExceptionsReportHandler(_context, Configuration(postingEnabled: false));

        var result = await handler.Handle(
            new GetVanSalesExceptionsReportQuery(From, From.AddDays(VanSalesFacts.MaximumDays + 1)),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.RangeTooWide", result.FirstError.Code);
    }

    // --- Helpers ---

    private async Task<VanSalesExceptionsReportResult> RunAsync(
        DateTime? from = null,
        DateTime? to = null,
        Guid? userId = null,
        bool postingEnabled = false)
    {
        var handler = new GetVanSalesExceptionsReportHandler(_context, Configuration(postingEnabled));

        var result = await handler.Handle(
            new GetVanSalesExceptionsReportQuery(from ?? From, to ?? To, userId),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>
    /// The posting switch, which is the difference between "nothing has posted" and "nothing could".
    /// </summary>
    private static IConfiguration Configuration(bool postingEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{VanSalesPostingSettings.SectionName}:Enabled"] = postingEnabled ? "true" : "false"
            })
            .Build();

    private static DateTime Utc(DateTime day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Utc);

    private void AddUser(Guid id, string username, string? firstName, string? lastName) =>
        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            FirstName = firstName,
            LastName = lastName,
            IsActive = true,
            AssignedWarehouseCode = VanWarehouse,
            AssignedBusinessPartnerCode = VanAccount
        });

    /// <summary>
    /// An online van invoice's local trace. A confirmed one is a sale; anything else is a document
    /// the rest of the suite cannot see.
    /// </summary>
    private void AddReservation(
        string reference,
        string status,
        DateTime createdAtUtc,
        decimal total,
        string currency = "USD",
        string? createdBy = null,
        string? paymentMethod = "Cash",
        string? routeCustomerCode = "TUCK01",
        string sourceSystem = SaleSourceSystems.VanSales,
        decimal originalQuantity = 1m,
        decimal? lineTotal = null) =>
        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = sourceSystem,
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : "Tuck Shop",
            TotalValue = total,
            Currency = currency,
            PaymentMethod = paymentMethod,
            Status = status,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddHours(1),
            ConfirmedAt = status == ReservationStatus.Confirmed ? createdAtUtc : null,
            CreatedBy = createdBy ?? Rep.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    OriginalQuantity = originalQuantity,
                    // CK_StockReservationLines_ReservedQuantity_Positive forbids a zero here, so a
                    // zero-quantity case has to be made on OriginalQuantity, which carries no such
                    // constraint — and OriginalQuantity is the column the fact reader reports.
                    ReservedQuantity = originalQuantity > 0 ? originalQuantity : 1m,
                    UoMCode = "EA",
                    WarehouseCode = VanWarehouse,
                    UnitPrice = originalQuantity > 0 ? total / originalQuantity : 0m,
                    LineTotal = lineTotal ?? total
                }
            ]
        });

    /// <summary>
    /// An offline van sale, uploaded from the handset and waiting on the posting job unless it is
    /// marked consolidated.
    /// </summary>
    private void AddOfflineSale(
        string reference,
        DateTime docDate,
        decimal total,
        string currency = "USD",
        string? createdBy = null,
        string? paymentMethod = "Cash",
        string? routeCustomerCode = "TUCK01",
        string sourceSystem = SaleSourceSystems.VanSales,
        DesktopSaleConsolidationStatus consolidation = DesktopSaleConsolidationStatus.Pending,
        int postingAttempts = 0,
        string? lastPostingError = null,
        DesktopSaleReceiptIngestStatus receiptStatus = DesktopSaleReceiptIngestStatus.NotApplicable,
        string? deviceSignature = "c2lnbmF0dXJl",
        decimal quantity = 1m,
        decimal? lineTotal = null) =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = sourceSystem,
            CardCode = VanAccount,
            CardName = "Van 010",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : "Tuck Shop",
            DocDate = docDate,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = VanWarehouse,
            PaymentMethod = paymentMethod,
            AmountPaid = total,
            ConsolidationStatus = consolidation,
            PostingAttempts = postingAttempts,
            LastPostingError = lastPostingError,
            ReceiptIngestStatus = receiptStatus,
            DeviceSignatureValue = deviceSignature,
            CreatedBy = createdBy ?? Rep.ToString(),
            Lines =
            [
                // CK_DesktopSaleLines_Quantity_Positive forbids a zero quantity on this path, so
                // every offline line here moves stock; only its value may be zero.
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    Quantity = quantity,
                    UoMCode = "EA",
                    UnitPrice = (lineTotal ?? total) / quantity,
                    LineTotal = lineTotal ?? total,
                    WarehouseCode = VanWarehouse
                }
            ]
        });
}
