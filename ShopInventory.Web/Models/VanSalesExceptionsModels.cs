using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// The van sales exception register, mirroring the API's <c>VanSalesExceptionsReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other API DTO in this project, which makes nullability the thing to get
/// right: a property declared non-nullable here against a value the API can send as null makes
/// System.Text.Json throw, and the page reports "no data" rather than an error. The computed
/// properties are re-derived here because computed properties are not serialised, and they are kept
/// identical to the API's definitions on purpose; if one is changed, both change.
///
/// The nulls in this report are all denominators that do not exist. A period that sold nothing has no
/// untendered share; a rep who took nothing in a currency has no cash share. Zero in either place
/// would read as perfect capture, which is the opposite of what the absence means.
///
/// Two things this report carries are worth stating where a reader of the DTOs will meet them.
/// <see cref="VanSalesUnseen"/> is read around the shared fact reader rather than through it — the
/// reader sees confirmed reservations only, so by construction it cannot show these — and it must
/// never be used as a second definition of a van sale. And
/// <see cref="VanSalesExceptionsQuality.PostingJobEnabled"/> has to be read alongside the held
/// figures, because "nothing has posted" means something very different when nothing is trying to.
/// </remarks>
public class VanSalesExceptionsReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public VanSalesExceptionsSummary Summary { get; set; } = new();
    public List<VanSalesTender> Tender { get; set; } = [];
    public List<VanSalesRepTender> TenderByRep { get; set; } = [];
    public List<VanSalesUnseen> Unseen { get; set; } = [];
    public List<VanSalesHeld> Held { get; set; } = [];
    public List<VanSalesReceiptHandover> ReceiptHandover { get; set; } = [];
    public List<VanSalesHygiene> Hygiene { get; set; } = [];
    public VanSalesExceptionsQuality Quality { get; set; } = new();
}

// ── Exposure ────────────────────────────────────────────────────────────────────

/// <summary>
/// Money on a document that is not a sale — a reservation that never confirmed, or a sale still
/// waiting to post.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="VanSalesMoney"/>, which is takings. Nothing here has been established
/// as revenue: some of it is money the estate holds and cannot see, and some of it is an abandoned
/// document that never represented a sale at all. Giving it the takings type would invite somebody to
/// add it to a sales figure, and there is no arithmetic in which that is correct.
/// </remarks>
public class VanSalesExposure
{
    public string Currency { get; set; } = string.Empty;
    public int DocumentCount { get; set; }
    public decimal Gross { get; set; }
}

// ── Summary ─────────────────────────────────────────────────────────────────────

public class VanSalesExceptionsSummary
{
    public int SaleCount { get; set; }
    public int RepCount { get; set; }
    public int SalesWithoutTender { get; set; }
    public int SalesWithoutOutlet { get; set; }
    public int LinesWithoutValue { get; set; }
    public int UnseenDocumentCount { get; set; }
    public int ExpiredDocumentCount { get; set; }
    public int HeldSaleCount { get; set; }
    public int? OldestHeldAgeDays { get; set; }
    public List<VanSalesExposure> UnseenExposure { get; set; } = [];
    public List<VanSalesExposure> HeldExposure { get; set; } = [];
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

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

// ── How the money was settled ───────────────────────────────────────────────────

/// <summary>
/// One tender, in one currency, over the whole period.
/// </summary>
/// <remarks>
/// The compliance report already splits tender per rep-day. This is the roll-up, which is a different
/// question: a day's split answers "did this rep's cash add up", and a period's split answers "what
/// is the fleet actually being paid in", which is a banking and float question.
///
/// <see cref="TenderName"/> carries the API's <c>Tender</c> field. It is renamed here and only here,
/// because C# forbids a member with the same name as its enclosing type; the
/// <see cref="JsonPropertyNameAttribute"/> keeps the wire contract exact.
///
/// Note that <c>Other</c> collects both a genuine swipe and a sale whose tender was never recorded at
/// all; <see cref="Untendered"/> separates the second out, because a tender nobody wrote down is a
/// capture failure and a swipe is a banking arrangement.
/// </remarks>
public class VanSalesTender
{
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("tender")]
    public string TenderName { get; set; } = string.Empty;

    public bool Untendered { get; set; }
    public int DocumentCount { get; set; }
    public decimal Gross { get; set; }

    public decimal? AverageDocumentValue =>
        DocumentCount > 0 ? decimal.Round(Gross / DocumentCount, 2) : null;
}

public class VanSalesRepTender
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int DocumentCount { get; set; }
    public decimal Gross { get; set; }
    public decimal CashGross { get; set; }
    public decimal EcocashGross { get; set; }
    public decimal InnbucksGross { get; set; }
    public decimal OtherGross { get; set; }
    public decimal UntenderedGross { get; set; }
    public int UntenderedCount { get; set; }

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
public class VanSalesUnseen
{
    public string Status { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public int DocumentCount { get; set; }
    public DateTime? EarliestCapturedAt { get; set; }
    public DateTime? LatestCapturedAt { get; set; }
    public List<VanSalesExposure> Exposure { get; set; } = [];

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
public class VanSalesHeld
{
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public int SaleCount { get; set; }
    public DateTime? OldestDocDate { get; set; }
    public int? OldestAgeDays { get; set; }
    public int AttemptedCount { get; set; }
    public int FailedCount { get; set; }
    public string? LastError { get; set; }
    public List<VanSalesExposure> Exposure { get; set; } = [];

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
public class VanSalesReceiptHandover
{
    public string Status { get; set; } = string.Empty;
    public int SaleCount { get; set; }
    public int WithSignature { get; set; }
    public DateTime? EarliestDocDate { get; set; }
    public DateTime? LatestDocDate { get; set; }

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
public class VanSalesHygiene
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public int SaleCount { get; set; }
    public int WithoutTender { get; set; }
    public int WithoutOutlet { get; set; }
    public int LineCount { get; set; }
    public int LinesWithoutValue { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? UntenderedRate => SaleCount > 0 ? (double)WithoutTender / SaleCount : null;

    public double? UnattributedRate => SaleCount > 0 ? (double)WithoutOutlet / SaleCount : null;

    /// <summary>True when this rep has nothing outstanding — used to sort a worklist, not to praise.</summary>
    public bool IsClean => WithoutTender == 0 && WithoutOutlet == 0 && LinesWithoutValue == 0;
}

// ── Quality ─────────────────────────────────────────────────────────────────────

public class VanSalesExceptionsQuality
{
    public int UnseenDocumentCount { get; set; }
    public int ExpiredDocumentCount { get; set; }
    public int HeldSaleCount { get; set; }
    public int HeldNeverAttemptedCount { get; set; }
    public int SalesWithoutTender { get; set; }
    public int SalesWithoutOutlet { get; set; }
    public int LinesWithoutValue { get; set; }
    public int ReceiptStatusesSeen { get; set; }
    public int ReceiptsWithoutSignature { get; set; }
    public bool PostingJobEnabled { get; set; }

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

            if (ReceiptStatusesSeen <= 1)
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
