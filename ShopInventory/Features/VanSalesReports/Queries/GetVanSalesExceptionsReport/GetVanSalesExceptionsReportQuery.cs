using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesExceptionsReport;

/// <summary>
/// How the money was settled, and every van document the rest of this suite cannot see.
/// </summary>
/// <remarks>
/// This report is deliberately not the "cash and leakage" report the reporting plan asked for. That
/// one cannot be built, and the reasons are worth stating here because they are the sort of thing a
/// reader will otherwise assume was an oversight:
///
/// <list type="bullet">
/// <item><b>There is no banked-cash table.</b> Declared-versus-banked is the comparison that catches
/// theft, and nothing in this system records what was banked. Declared-versus-system is the only
/// variance the schema supports, and it already ships per rep-day on the compliance page.</item>
/// <item><b>There is no discount dimension.</b> Neither van ingest path writes
/// <c>DiscountPercent</c>, so every van line reads 0.00%. The performance report's peer-relative
/// price realisation stands in for it.</item>
/// <item><b>There is no returns reason.</b> The credit-note projection mirrors SAP's
/// <c>U_Reasons</c>, but nothing in this system ever writes it, and no van endpoint can capture a
/// return at all.</item>
/// </list>
///
/// What is left is the half nobody asked for and everybody needs: the documents that fall out of the
/// reporting union. Two of them matter enough to name.
///
/// <b>A van sale made while SAP is unavailable never becomes a sale.</b> The invoice is queued, its
/// reservation stays <c>Pending</c>, the cleanup job expires it within the hour, and
/// <see cref="VanSalesFactReader"/> reads only <c>Confirmed</c> reservations. So the money leaves
/// every van report — and the reports read <em>lower</em>, which is to say better, exactly when the
/// estate is having its worst day. That is the single most misleading thing this suite does, and the
/// only place it is visible is here.
///
/// <b>An offline van sale is held until a posting job drains it.</b> If that job is not running the
/// sales pile up correctly and invisibly. This report counts them, but it deliberately reports the
/// state of the posting switch alongside, because "nothing has posted" means something very
/// different when nothing is trying to.
/// </remarks>
public sealed record GetVanSalesExceptionsReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null
) : IRequest<ErrorOr<VanSalesExceptionsReportResult>>;

public sealed record VanSalesExceptionsReportResult(
    DateTime FromDate,
    DateTime ToDate,
    VanSalesExceptionsSummaryResult Summary,
    List<VanSalesTenderResult> Tender,
    List<VanSalesRepTenderResult> TenderByRep,
    List<VanSalesUnseenResult> Unseen,
    List<VanSalesHeldResult> Held,
    List<VanSalesReceiptHandoverResult> ReceiptHandover,
    List<VanSalesHygieneResult> Hygiene,
    VanSalesExceptionsQualityResult Quality
);

/// <summary>
/// Money on a document that is not a sale — a reservation that never confirmed, or a sale still
/// waiting to post.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="VanSalesMoneyResult"/>, which is takings. Nothing here has been
/// established as revenue: some of it is money the estate holds and cannot see, and some of it is an
/// abandoned document that never represented a sale at all. Giving it the takings type would invite
/// somebody to add it to a sales figure, and there is no arithmetic in which that is correct.
/// </remarks>
public sealed record VanSalesExposureResult(
    string Currency,
    int DocumentCount,
    decimal Gross);

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanSalesExceptionsSummaryResult(
    int SaleCount,
    int RepCount,
    int SalesWithoutTender,
    int SalesWithoutOutlet,
    int LinesWithoutValue,
    int UnseenDocumentCount,
    int ExpiredDocumentCount,
    int HeldSaleCount,
    int? OldestHeldAgeDays,
    List<VanSalesExposureResult> UnseenExposure,
    List<VanSalesExposureResult> HeldExposure,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    /// <summary>
    /// The share of settled sales whose tender was never recorded. Null when nothing sold — a period
    /// with no sales has no untendered share, and 0% would read as perfect capture.
    /// </summary>
    public double? UntenderedRate =>
        SaleCount > 0 ? (double)SalesWithoutTender / SaleCount : null;

    /// <summary>
    /// Unseen documents as a share of everything captured, settled or not. This is the figure that
    /// says how much of the estate's day the rest of the suite is describing.
    /// </summary>
    public double? UnseenRate =>
        SaleCount + UnseenDocumentCount > 0
            ? (double)UnseenDocumentCount / (SaleCount + UnseenDocumentCount)
            : null;
}

// ── D2: how the money was settled ───────────────────────────────────────────────

/// <summary>
/// One tender, in one currency, over the whole period.
/// </summary>
/// <remarks>
/// The compliance report already splits tender per rep-day. This is the roll-up, which is a different
/// question: a day's split answers "did this rep's cash add up", and a period's split answers "what
/// is the fleet actually being paid in", which is a banking and float question.
///
/// Both read <c>VanSalesFacts.ClassifyTender</c>, so the two can never disagree about what counts as
/// cash. A sale whose tender was never recorded is its own member of that classification rather than
/// a shade of <c>Other</c> — a tender nobody wrote down is a capture failure and a swipe is a
/// banking arrangement, and the five buckets partition the takings rather than overlapping.
/// <see cref="Untendered"/> is carried on the row as well so a reader does not have to know the
/// member names to spot it.
/// </remarks>
public sealed record VanSalesTenderResult(
    string Currency,
    string Tender,
    bool Untendered,
    int DocumentCount,
    decimal Gross)
{
    public decimal? AverageDocumentValue =>
        DocumentCount > 0 ? decimal.Round(Gross / DocumentCount, 2) : null;
}

public sealed record VanSalesRepTenderResult(
    Guid UserId,
    string Username,
    string? FullName,
    string Currency,
    int DocumentCount,
    decimal Gross,
    decimal CashGross,
    decimal EcocashGross,
    decimal InnbucksGross,
    decimal OtherGross,
    decimal UntenderedGross,
    int UntenderedCount)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    /// <summary>
    /// What share of this rep's takings came in as cash — the figure that decides how much physical
    /// money is expected back. Null when the rep took nothing in this currency.
    /// </summary>
    public double? CashShare => Gross > 0 ? (double)(CashGross / Gross) : null;

    /// <summary>
    /// The share this rep settled without recording how. Null on no takings, for the same reason.
    /// </summary>
    public double? UntenderedShare => Gross > 0 ? (double)(UntenderedGross / Gross) : null;
}

// ── The documents the suite cannot see ──────────────────────────────────────────

/// <summary>
/// Van documents captured in the window that never became sales, grouped by the state they stopped
/// in.
/// </summary>
/// <remarks>
/// Read straight from <c>StockReservations</c> rather than through the shared fact reader, and that
/// is the whole point of the section: the reader filters to <c>Confirmed</c>, so by construction it
/// cannot show these. This is the one report in the suite that reaches around it, and it must never
/// be used as a second definition of a van sale.
///
/// The states are not equally interesting, and the report does not pretend they are:
/// <list type="bullet">
/// <item><c>Expired</c> is the outage signature. A van invoice that could not reach SAP is queued and
/// its reservation is left <c>Pending</c>; the cleanup job then expires it. The sale itself was made
/// — the customer was served — and it reaches SAP later by a different route, but no van report will
/// ever count it.</item>
/// <item><c>Pending</c> is either an outage in progress or a document written moments ago.</item>
/// <item><c>Cancelled</c> and <c>Failed</c> are set when a posting attempt failed outright.</item>
/// </list>
/// </remarks>
public sealed record VanSalesUnseenResult(
    string Status,
    Guid? UserId,
    string? Username,
    string? FullName,
    int DocumentCount,
    DateTime? EarliestCapturedAt,
    DateTime? LatestCapturedAt,
    List<VanSalesExposureResult> Exposure)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName)
            ? string.IsNullOrWhiteSpace(Username) ? "Unattributed" : Username
            : FullName;

    /// <summary>
    /// Whether this row is the outage signature rather than an ordinary abandoned document. Named on
    /// the record so the page and the workbook cannot disagree about which rows are the alarming ones.
    /// </summary>
    public bool IsLostSale => string.Equals(Status, "Expired", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One rep's offline sales that have been ingested and not yet posted to SAP.</summary>
public sealed record VanSalesHeldResult(
    Guid? UserId,
    string? Username,
    string? FullName,
    int SaleCount,
    DateTime? OldestDocDate,
    int? OldestAgeDays,
    int AttemptedCount,
    int FailedCount,
    string? LastError,
    List<VanSalesExposureResult> Exposure)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName)
            ? string.IsNullOrWhiteSpace(Username) ? "Unattributed" : Username
            : FullName;

    /// <summary>
    /// Held sales that no posting attempt has ever touched. When this equals <see cref="SaleCount"/>
    /// across the whole report, the question is whether the posting job is running at all rather than
    /// whether any particular sale is stuck.
    /// </summary>
    public int NeverAttemptedCount => SaleCount - AttemptedCount;
}

// ── The fiscal receipt handover ─────────────────────────────────────────────────

/// <summary>
/// How many van sales sit in each receipt-handover state.
/// </summary>
/// <remarks>
/// A distribution, deliberately, and not a count of exceptions. Every value here is decided by
/// fields the handset uploads, and this repository cannot confirm which build the fleet is running —
/// so a report that declared "N receipts are broken" could as easily be describing an old handset as
/// a real fiscal problem. Showing the whole distribution lets a reader see a one-valued column for
/// what it is.
///
/// <c>NotApplicable</c> deserves particular suspicion. The column was added with that as its default
/// and no backfill, and the drain skips it — so a van sale ingested before the column existed carries
/// a value that means "nothing to submit" while its receipt will in fact never be submitted.
/// </remarks>
public sealed record VanSalesReceiptHandoverResult(
    string Status,
    int SaleCount,
    int WithSignature,
    DateTime? EarliestDocDate,
    DateTime? LatestDocDate)
{
    /// <summary>
    /// Rows carrying no device signature at all cannot be handed over whatever their status says.
    /// </summary>
    public int WithoutSignature => SaleCount - WithSignature;
}

// ── Capture hygiene ─────────────────────────────────────────────────────────────

/// <summary>
/// Per rep, the things a sale should carry and did not. Every figure is a capture failure, not a
/// money failure — the sale is real and the money is real; something about it was never written down.
/// </summary>
public sealed record VanSalesHygieneResult(
    Guid UserId,
    string Username,
    string? FullName,
    int SaleCount,
    int WithoutTender,
    int WithoutOutlet,
    int LineCount,
    int LinesWithoutValue)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? UntenderedRate => SaleCount > 0 ? (double)WithoutTender / SaleCount : null;

    public double? UnattributedRate => SaleCount > 0 ? (double)WithoutOutlet / SaleCount : null;

    /// <summary>True when this rep has nothing outstanding — used to sort a worklist, not to praise.</summary>
    public bool IsClean => WithoutTender == 0 && WithoutOutlet == 0 && LinesWithoutValue == 0;
}

// ── Quality ─────────────────────────────────────────────────────────────────────

public sealed record VanSalesExceptionsQualityResult(
    int UnseenDocumentCount,
    int ExpiredDocumentCount,
    int HeldSaleCount,
    int HeldNeverAttemptedCount,
    int SalesWithoutTender,
    int SalesWithoutOutlet,
    int LinesWithoutValue,
    int ReceiptStatusesSeen,
    int ReceiptsWithoutSignature,
    bool PostingJobEnabled)
{
    public bool IsClean =>
        UnseenDocumentCount == 0
        && HeldSaleCount == 0
        && SalesWithoutTender == 0
        && SalesWithoutOutlet == 0
        && LinesWithoutValue == 0
        && ReceiptsWithoutSignature == 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (!PostingJobEnabled)
            {
                yield return
                    "The van sales posting job is switched off in this environment, so no offline sale "
                    + "can reach SAP however long it waits. Read the held figures as the size of the "
                    + "queue, not as a posting failure.";
            }

            if (ExpiredDocumentCount > 0)
            {
                yield return
                    $"{ExpiredDocumentCount:N0} van invoice(s) expired without confirming. These are "
                    + "sales that were made and served but that no van report counts, because the "
                    + "reporting union reads confirmed documents only. They are the usual signature of "
                    + "a period when SAP was unreachable.";
            }

            if (UnseenDocumentCount > ExpiredDocumentCount)
            {
                yield return
                    $"{UnseenDocumentCount - ExpiredDocumentCount:N0} further van document(s) are still "
                    + "pending, cancelled or failed. A recent pending document is ordinary; an old one "
                    + "is not.";
            }

            if (HeldNeverAttemptedCount > 0 && HeldNeverAttemptedCount == HeldSaleCount)
            {
                yield return
                    "Every held sale has a posting attempt count of zero, so nothing has tried to post "
                    + "them. That is a job that is not running rather than a set of sales that failed.";
            }

            if (SalesWithoutTender > 0)
            {
                yield return
                    $"{SalesWithoutTender:N0} sale(s) record no payment method. Their money is in the "
                    + "totals but not in the tender split, so the two will not reconcile.";
            }

            if (SalesWithoutOutlet > 0)
            {
                yield return
                    $"{SalesWithoutOutlet:N0} sale(s) name no outlet and are reported as unattributed "
                    + "rather than credited to a shop.";
            }

            if (LinesWithoutValue > 0)
            {
                yield return
                    $"{LinesWithoutValue:N0} line(s) carry a quantity and no value. Nothing in the "
                    + "capture path forbids it, so this is a real zero rather than a rounding artefact.";
            }

            if (ReceiptsWithoutSignature > 0)
            {
                yield return
                    $"{ReceiptsWithoutSignature:N0} van sale(s) carry no device signature, so their "
                    + "ZIMRA receipt can never be handed to the fiscalisation platform whatever their "
                    + "handover status says.";
            }

            // Exactly one, not "at most one". A period with no van sales at all has no status to
            // share, and saying every sale shares one would be a claim about an empty set.
            if (ReceiptStatusesSeen == 1)
            {
                yield return
                    "Every van sale in this period shares one receipt-handover status. That is what a "
                    + "handset build predating the signed-receipt upload looks like, so read the "
                    + "handover section as unestablished rather than as good news.";
            }

            yield return
                "Declared cash is not compared here. Nothing records what was banked, so the only "
                + "variance the data supports is declared against system, which the compliance report "
                + "already gives per rep per day.";
        }
    }
}
