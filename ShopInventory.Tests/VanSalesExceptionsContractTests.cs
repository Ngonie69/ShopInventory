using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesExceptionsReport;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Sends the exception register across the wire and reads it back as the portal's hand-mirrored
/// DTOs.
/// </summary>
/// <remarks>
/// Same guard as the other van reports, and the report that can least afford to fail quietly. A
/// property declared non-nullable on the portal side against a value the API can send as null makes
/// System.Text.Json throw inside <c>GetFromJsonAsync</c>; the service's catch turns that into a null
/// return, and the page renders "no data". On this report that reads as an estate with nothing
/// hidden, when what has actually happened is that nobody is looking.
///
/// Every null here is an absence rather than a zero. A rep who took nothing in a currency has no cash
/// share, a period that sold nothing has no untendered share, a held sale whose date was never
/// written has no age, and a document nobody attributed has no rep. Zero in any of those places reads
/// as a clean result, which is the opposite of what the absence means.
///
/// The last test compares the two <c>Caveats</c> implementations word for word. They are two
/// hand-written copies of the same prose in two projects, nothing makes them agree, and a reader has
/// no way to tell which copy is on the screen in front of them.
/// </remarks>
public class VanSalesExceptionsContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly Guid RepId = Guid.Parse("2f9c3f7a-9d5c-4f0e-9d2a-6d1c0b8a4e11");

    private static T RoundTrip<TSource, T>(TSource result)
    {
        var mirrored = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result, Wire), Wire);
        Assert.NotNull(mirrored);
        return mirrored;
    }

    // ── The whole report ────────────────────────────────────────────────────────

    /// <summary>
    /// A populated register has to arrive with every figure in the place it left from. A property the
    /// mirror spells differently does not throw — it arrives as zero, and a zero on this report is
    /// indistinguishable from good news.
    /// </summary>
    [Fact]
    public void A_populated_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip<VanSalesExceptionsReportResult, VanSalesExceptionsReportResponse>(
            Populated());

        Assert.Equal(new DateTime(2026, 8, 1), mirrored.FromDate);
        Assert.Equal(new DateTime(2026, 8, 31), mirrored.ToDate);

        Assert.Equal(250, mirrored.Summary.SaleCount);
        Assert.Equal(50, mirrored.Summary.UnseenDocumentCount);
        Assert.Equal(31, mirrored.Summary.ExpiredDocumentCount);
        Assert.Equal(46, mirrored.Summary.OldestHeldAgeDays);
        Assert.Equal(0.04, mirrored.Summary.UntenderedRate!.Value, 4);
        Assert.Equal(1d / 6d, mirrored.Summary.UnseenRate!.Value, 6);
        Assert.Equal(2_240m, Assert.Single(mirrored.Summary.UnseenExposure).Gross);
        Assert.Equal(640m, Assert.Single(mirrored.Summary.HeldExposure).Gross);
        Assert.Equal(40m, Assert.Single(mirrored.Summary.TotalsByCurrency).AverageDropSize);

        // Tender is the one field renamed in the mirror — C# forbids a member named as its enclosing
        // type — so it is the one field where a wrong wire name arrives as an empty string in silence.
        var cash = mirrored.Tender.Single(row => row.TenderName == "Cash");
        Assert.False(cash.Untendered);
        Assert.Equal(200, cash.DocumentCount);
        Assert.Equal(30m, cash.AverageDocumentValue);

        var untendered = mirrored.Tender.Single(row => row.Untendered);
        Assert.Equal("Other", untendered.TenderName);
        Assert.Equal(40m, untendered.AverageDocumentValue);

        var rep = Assert.Single(mirrored.TenderByRep);
        Assert.Equal(RepId, rep.UserId);
        Assert.Equal("Tinashe Moyo", rep.DisplayName);
        Assert.Equal(0.75, rep.CashShare!.Value, 4);
        Assert.Equal(0.05, rep.UntenderedShare!.Value, 4);
        Assert.Equal(10, rep.UntenderedCount);

        // The outage signature. This row is the only place in the suite these sales are counted.
        var expired = mirrored.Unseen[0];
        Assert.Equal("Expired", expired.Status);
        Assert.True(expired.IsLostSale);
        Assert.Equal(31, expired.DocumentCount);
        Assert.Equal(new DateTime(2026, 8, 12, 9, 0, 0), expired.EarliestCapturedAt);
        Assert.Equal(1_410m, Assert.Single(expired.Exposure).Gross);

        var pending = mirrored.Unseen[1];
        Assert.False(pending.IsLostSale);
        Assert.Equal("Unattributed", pending.DisplayName);

        var held = Assert.Single(mirrored.Held);
        Assert.Equal(18, held.SaleCount);
        Assert.Equal(new DateTime(2026, 7, 3), held.OldestDocDate);
        Assert.Equal(46, held.OldestAgeDays);
        Assert.Equal(14, held.NeverAttemptedCount);
        Assert.Equal("SAP connection closed", held.LastError);

        var handover = mirrored.ReceiptHandover[0];
        Assert.Equal("NotApplicable", handover.Status);
        Assert.Equal(new DateTime(2026, 8, 31), handover.LatestDocDate);
        Assert.Equal(60, handover.WithoutSignature);

        var hygiene = Assert.Single(mirrored.Hygiene);
        Assert.Equal("van010", hygiene.Username);
        Assert.Equal(1_000, hygiene.LineCount);
        Assert.Equal(0.04, hygiene.UntenderedRate!.Value, 4);
        Assert.Equal(0.02, hygiene.UnattributedRate!.Value, 4);
        Assert.False(hygiene.IsClean);

        // The switch, which decides how the held figures are to be read at all.
        Assert.False(mirrored.Quality.PostingJobEnabled);
        Assert.False(mirrored.Quality.IsClean);
    }

    /// <summary>
    /// Every null the API can send, sent at once. This is the case that breaks a hand-written mirror,
    /// and it is the case a live period produces routinely: a document written by a rep the user table
    /// no longer knows, a held sale nothing has tried to post, a handover status carrying no dates.
    /// </summary>
    [Fact]
    public void Every_null_the_api_can_send_survives_the_mirror()
    {
        var mirrored = RoundTrip<VanSalesExceptionsReportResult, VanSalesExceptionsReportResponse>(
            AllNulls());

        // No held sale carries a date, so the estate's oldest held sale has no age.
        Assert.Null(mirrored.Summary.OldestHeldAgeDays);

        // Nothing sold, so there is no untendered share. The unseen share, by contrast, is the whole
        // of it: every van document in this period is one the rest of the suite cannot see.
        Assert.Null(mirrored.Summary.UntenderedRate);
        Assert.Equal(1d, mirrored.Summary.UnseenRate!.Value, 6);

        var tender = Assert.Single(mirrored.Tender);
        Assert.Null(tender.AverageDocumentValue);

        var rep = Assert.Single(mirrored.TenderByRep);
        Assert.Null(rep.FullName);
        Assert.Equal("van010", rep.DisplayName);
        Assert.Null(rep.CashShare);
        Assert.Null(rep.UntenderedShare);

        var unseen = Assert.Single(mirrored.Unseen);
        Assert.Null(unseen.UserId);
        Assert.Null(unseen.Username);
        Assert.Null(unseen.FullName);
        Assert.Null(unseen.EarliestCapturedAt);
        Assert.Null(unseen.LatestCapturedAt);
        Assert.Equal("Unattributed", unseen.DisplayName);
        Assert.False(unseen.IsLostSale);
        Assert.Empty(unseen.Exposure);

        var held = Assert.Single(mirrored.Held);
        Assert.Null(held.UserId);
        Assert.Null(held.Username);
        Assert.Null(held.FullName);
        Assert.Null(held.OldestDocDate);
        Assert.Null(held.OldestAgeDays);
        Assert.Null(held.LastError);
        Assert.Equal("Unattributed", held.DisplayName);
        Assert.Equal(1, held.NeverAttemptedCount);
        Assert.Empty(held.Exposure);

        var handover = Assert.Single(mirrored.ReceiptHandover);
        Assert.Null(handover.EarliestDocDate);
        Assert.Null(handover.LatestDocDate);

        var hygiene = Assert.Single(mirrored.Hygiene);
        Assert.Null(hygiene.FullName);
        Assert.Null(hygiene.UntenderedRate);
        Assert.Null(hygiene.UnattributedRate);
    }

    /// <summary>
    /// An empty period must arrive with empty lists, or the page throws on its first loop — and with
    /// null rates, because a period with no sales has no shares of anything.
    /// </summary>
    [Fact]
    public void An_empty_report_arrives_with_empty_lists_and_null_rates()
    {
        var mirrored = RoundTrip<VanSalesExceptionsReportResult, VanSalesExceptionsReportResponse>(
            Empty());

        Assert.Empty(mirrored.Tender);
        Assert.Empty(mirrored.TenderByRep);
        Assert.Empty(mirrored.Unseen);
        Assert.Empty(mirrored.Held);
        Assert.Empty(mirrored.ReceiptHandover);
        Assert.Empty(mirrored.Hygiene);
        Assert.Empty(mirrored.Summary.UnseenExposure);
        Assert.Empty(mirrored.Summary.HeldExposure);
        Assert.Empty(mirrored.Summary.TotalsByCurrency);

        Assert.Null(mirrored.Summary.OldestHeldAgeDays);
        Assert.Null(mirrored.Summary.UntenderedRate);
        Assert.Null(mirrored.Summary.UnseenRate);

        Assert.True(mirrored.Quality.IsClean);

        // The declared-cash limitation is unconditional. It is the one thing a reader has to carry
        // into every figure on this report, including a report with no figures on it.
        Assert.Contains(mirrored.Quality.Caveats, caveat => caveat.Contains("Declared cash is not compared"));
    }

    // ── The caveats, which exist twice ──────────────────────────────────────────

    /// <summary>
    /// The API's caveats and the portal's are two hand-written copies of the same prose, and drift
    /// between them is invisible: both sides compile, both sides render, and the sentence a manager
    /// reads is simply not the sentence the report meant. Every branch is walked and compared word for
    /// word, in order.
    /// </summary>
    [Fact]
    public void The_caveats_are_word_for_word_identical_on_both_sides()
    {
        foreach (var quality in EveryCaveatShape())
        {
            var mirrored = RoundTrip<VanSalesExceptionsQualityResult, VanSalesExceptionsQuality>(quality);

            Assert.Equal(quality.IsClean, mirrored.IsClean);
            Assert.Equal(quality.Caveats, mirrored.Caveats);
            Assert.NotEmpty(mirrored.Caveats);
        }
    }

    /// <summary>
    /// The worst period this report can describe: the posting job off, an outage's worth of expired
    /// invoices, held sales nothing has attempted, and a fleet on a handset build that reports one
    /// receipt status. Every branch fires, and the switched-off job leads, because it is the fact that
    /// decides how the rest of the section is to be read.
    /// </summary>
    [Fact]
    public void A_period_with_everything_wrong_carries_every_caveat_with_the_switch_first()
    {
        var mirrored = RoundTrip<VanSalesExceptionsQualityResult, VanSalesExceptionsQuality>(
            new VanSalesExceptionsQualityResult(
                UnseenDocumentCount: 50,
                ExpiredDocumentCount: 31,
                HeldSaleCount: 18,
                HeldNeverAttemptedCount: 18,
                SalesWithoutTender: 10,
                SalesWithoutOutlet: 5,
                LinesWithoutValue: 6,
                ReceiptStatusesSeen: 1,
                ReceiptsWithoutSignature: 60,
                PostingJobEnabled: false));

        var caveats = mirrored.Caveats.ToList();

        Assert.Equal(10, caveats.Count);
        Assert.StartsWith("The van sales posting job is switched off", caveats[0]);
        Assert.Contains(caveats, caveat => caveat.Contains("31 van invoice(s) expired without confirming"));
        Assert.Contains(caveats, caveat => caveat.Contains("19 further van document(s)"));
        Assert.Contains(caveats, caveat => caveat.Contains("nothing has tried to post them"));
        Assert.False(mirrored.IsClean);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static VanSalesExceptionsReportResult Populated() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        Summary: new VanSalesExceptionsSummaryResult(
            SaleCount: 250,
            RepCount: 1,
            SalesWithoutTender: 10,
            SalesWithoutOutlet: 5,
            LinesWithoutValue: 6,
            UnseenDocumentCount: 50,
            ExpiredDocumentCount: 31,
            HeldSaleCount: 18,
            OldestHeldAgeDays: 46,
            UnseenExposure: [new VanSalesExposureResult("USD", 50, 2_240m)],
            HeldExposure: [new VanSalesExposureResult("USD", 18, 640m)],
            TotalsByCurrency: [new VanSalesMoneyResult("USD", 250, 200, 8_000m)]),
        Tender:
        [
            new VanSalesTenderResult("USD", "Cash", Untendered: false, 200, 6_000m),
            new VanSalesTenderResult("USD", "Ecocash", Untendered: false, 40, 1_600m),
            new VanSalesTenderResult("USD", "Other", Untendered: true, 10, 400m)
        ],
        TenderByRep:
        [
            new VanSalesRepTenderResult(
                UserId: RepId,
                Username: "van010",
                FullName: "Tinashe Moyo",
                Currency: "USD",
                DocumentCount: 250,
                Gross: 8_000m,
                CashGross: 6_000m,
                EcocashGross: 1_600m,
                InnbucksGross: 0m,
                OtherGross: 400m,
                UntenderedGross: 400m,
                UntenderedCount: 10)
        ],
        Unseen:
        [
            new VanSalesUnseenResult(
                "Expired", RepId, "van010", "Tinashe Moyo", 31,
                new DateTime(2026, 8, 12, 9, 0, 0),
                new DateTime(2026, 8, 12, 16, 0, 0),
                [new VanSalesExposureResult("USD", 31, 1_410m)]),
            new VanSalesUnseenResult(
                "Pending", null, null, null, 19,
                new DateTime(2026, 8, 31, 15, 0, 0),
                new DateTime(2026, 8, 31, 17, 0, 0),
                [new VanSalesExposureResult("USD", 19, 830m)])
        ],
        Held:
        [
            new VanSalesHeldResult(
                UserId: RepId,
                Username: "van010",
                FullName: "Tinashe Moyo",
                SaleCount: 18,
                OldestDocDate: new DateTime(2026, 7, 3),
                OldestAgeDays: 46,
                AttemptedCount: 4,
                FailedCount: 2,
                LastError: "SAP connection closed",
                Exposure: [new VanSalesExposureResult("USD", 18, 640m)])
        ],
        ReceiptHandover:
        [
            new VanSalesReceiptHandoverResult(
                "NotApplicable", 230, 170, new DateTime(2026, 8, 1), new DateTime(2026, 8, 31)),
            new VanSalesReceiptHandoverResult(
                "Submitted", 20, 20, new DateTime(2026, 8, 20), new DateTime(2026, 8, 31))
        ],
        Hygiene:
        [
            new VanSalesHygieneResult(RepId, "van010", "Tinashe Moyo", 250, 10, 5, 1_000, 6)
        ],
        Quality: new VanSalesExceptionsQualityResult(50, 31, 18, 14, 10, 5, 6, 2, 60, false));

    /// <summary>
    /// A period whose only van documents are ones nobody can attribute and nothing has posted. Every
    /// nullable the API declares is null here at the same time.
    /// </summary>
    private static VanSalesExceptionsReportResult AllNulls() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        Summary: new VanSalesExceptionsSummaryResult(
            SaleCount: 0,
            RepCount: 1,
            SalesWithoutTender: 0,
            SalesWithoutOutlet: 0,
            LinesWithoutValue: 0,
            UnseenDocumentCount: 1,
            ExpiredDocumentCount: 0,
            HeldSaleCount: 1,
            OldestHeldAgeDays: null,
            UnseenExposure: [],
            HeldExposure: [],
            TotalsByCurrency: []),
        Tender: [new VanSalesTenderResult("USD", "Other", Untendered: true, 0, 0m)],
        TenderByRep:
        [
            new VanSalesRepTenderResult(
                RepId, "van010", null, "USD", 0, 0m, 0m, 0m, 0m, 0m, 0m, 0)
        ],
        Unseen: [new VanSalesUnseenResult("Pending", null, null, null, 1, null, null, [])],
        Held: [new VanSalesHeldResult(null, null, null, 1, null, null, 0, 0, null, [])],
        ReceiptHandover: [new VanSalesReceiptHandoverResult("NotApplicable", 1, 0, null, null)],
        Hygiene: [new VanSalesHygieneResult(RepId, "van010", null, 0, 0, 0, 0, 0)],
        Quality: new VanSalesExceptionsQualityResult(1, 0, 1, 1, 0, 0, 0, 1, 1, false));

    private static VanSalesExceptionsReportResult Empty() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        Summary: new VanSalesExceptionsSummaryResult(
            0, 0, 0, 0, 0, 0, 0, 0, null, [], [], []),
        Tender: [],
        TenderByRep: [],
        Unseen: [],
        Held: [],
        ReceiptHandover: [],
        Hygiene: [],
        Quality: new VanSalesExceptionsQualityResult(0, 0, 0, 0, 0, 0, 0, 0, 0, true));

    /// <summary>One shape per branch of the caveat list, and one with all of them at once.</summary>
    private static IEnumerable<VanSalesExceptionsQualityResult> EveryCaveatShape()
    {
        // Nothing outstanding and the posting job running: the unconditional caveat only.
        yield return new VanSalesExceptionsQualityResult(0, 0, 0, 0, 0, 0, 0, 3, 0, true);

        // The switch off, with a queue behind it.
        yield return new VanSalesExceptionsQualityResult(0, 0, 12, 0, 0, 0, 0, 3, 0, false);

        // An outage: expired invoices, and further documents still pending on top of them.
        yield return new VanSalesExceptionsQualityResult(50, 31, 0, 0, 0, 0, 0, 3, 0, true);

        // Held sales that nothing has ever attempted, which is a job question and not a sale question.
        yield return new VanSalesExceptionsQualityResult(0, 0, 18, 18, 0, 0, 0, 3, 0, true);

        // Capture failures: no tender, no outlet, no line value.
        yield return new VanSalesExceptionsQualityResult(0, 0, 0, 0, 10, 5, 6, 3, 0, true);

        // A fleet reporting one receipt status, and receipts carrying no device signature.
        yield return new VanSalesExceptionsQualityResult(0, 0, 0, 0, 0, 0, 0, 1, 60, true);

        // Every branch at once.
        yield return new VanSalesExceptionsQualityResult(50, 31, 18, 18, 10, 5, 6, 1, 60, false);
    }
}
