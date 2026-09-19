namespace ShopInventory.Configuration;

/// <summary>
/// How shop till and vending sales are carried to SAP after they have been fiscalised.
/// </summary>
/// <remarks>
/// Tuning only. There is deliberately no on/off switch: the sources this route claims are excluded
/// from the 18:00 consolidation unconditionally, so a way to turn the route off would be a way to
/// leave a day's fiscalised takings claimed by nobody, with nothing logged. The job is registered
/// whenever SAP is enabled.
/// </remarks>
public class DesktopSalePostingSettings
{
    public const string SectionName = "DesktopSalePosting";

    /// <summary>
    /// How often to sweep for sales waiting to post. Every minute keeps SAP within a minute or two of
    /// the till; the cost is a SAP round trip per pass that finds work.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How often to sign the sales that were stored unsigned — vending, which prints nothing. Faster
    /// than the posting interval because nothing can post until it has fiscalised, so this delay is
    /// added to that one.
    /// </summary>
    public int FiscalisationIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// An end-of-day mop-up, in case a sale spent the day failing.
    /// </summary>
    public string SweepTimeCAT { get; set; } = "20:00";

    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// After this many non-transient failures a sale stops being retried and waits for a human. A
    /// transient failure — SAP down, a timeout — does not count against it, or a short outage would
    /// exhaust the budget for the whole day's takings within minutes.
    /// </summary>
    public int MaxPostingAttempts { get; set; } = 6;

    /// <summary>
    /// After this many attempts a vending sale stops being offered to the fiscalisation platform and
    /// waits for a human. Separate from <see cref="MaxPostingAttempts"/> because the two failures are
    /// different: a SAP post can be retried freely, while every fiscalisation attempt is a submission
    /// to ZIMRA.
    /// </summary>
    public int MaxFiscalisationAttempts { get; set; } = 5;

    /// <summary>
    /// How far back a pass looks. More than a day so a sale that failed just before midnight is not
    /// stranded when the date rolls over.
    /// </summary>
    public int LookbackDays { get; set; } = 3;

    /// <summary>
    /// How long after a post was issued the system waits before it will send that sale again,
    /// when SAP shows no invoice for it.
    /// </summary>
    /// <remarks>
    /// The duplicate guard is a read, and a read can miss a write SAP has committed but not yet made
    /// visible to a filter on a UDF. Five sales were invoiced twice on KEFALOS_USD_NEW2 between 25
    /// August and 8 September 2026 that way, each pair between nought and three minutes apart.
    ///
    /// <para>
    /// So the lookup's "no invoice" is only trustworthy once enough time has passed for that lag to
    /// have cleared. Fifteen minutes is several times the largest gap observed, and the cost of
    /// waiting is a sale posting a quarter of an hour later than it might have — against a duplicate
    /// fiscal receipt, which cannot be withdrawn from ZIMRA and needs a manual credit note.
    /// </para>
    ///
    /// <para>Set to zero to restore the old behaviour of reposting immediately.</para>
    /// </remarks>
    public int UnresolvedPostGraceMinutes { get; set; } = 15;

    /// <summary>
    /// How long a sale's post claim outlives the process holding it. Covers van sales too: one guard
    /// serves both routes.
    /// </summary>
    /// <remarks>
    /// A running post renews its claim every quarter of this, however long SAP takes, so this is not
    /// a limit on a post — it is how soon a claim whose owner died (an app-pool recycle or a deploy
    /// mid-post) stops refusing "a post to SAP for this sale is already in progress". It used to be
    /// the idempotency expiry, an hour.
    ///
    /// <para>
    /// Shortening it cannot let a second invoice through. A claim lapses only once nobody has renewed
    /// it for this long, so its owner is gone; and a post that reached SAP before dying left
    /// <c>PostIssuedAtUtc</c> behind, which holds the sale for <see cref="UnresolvedPostGraceMinutes"/>
    /// and has the next attempt ask SAP first.
    /// </para>
    ///
    /// <para>Zero or less restores the old behaviour: no renewal, and the idempotency expiry.</para>
    /// </remarks>
    public int PostClaimLeaseSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the daily incoming payment runs, until an admin saves the switch from Web → Settings.
    /// Off: desktop sales post their invoices only, and every invoice stays open in SAP.
    /// </summary>
    /// <remarks>
    /// Only the fallback: once saved, the <c>SystemConfigs</c> row read by
    /// <see cref="ShopInventory.Services.DailyIncomingPaymentSwitch"/> wins. Halted 2026-09-18 at the
    /// business's request, and off by default so that a missing key cannot quietly restart payments.
    /// Turning it back on settles the backlog, but only as far back as
    /// <see cref="DailyPaymentLookbackDays"/> days.
    /// </remarks>
    public bool DailyPaymentEnabled { get; set; }

    /// <summary>
    /// When (CAT) each business partner's daily incoming payment is posted.
    /// </summary>
    /// <remarks>
    /// Invoices posted to SAP before this time are settled on that day's payment. Anything posted after
    /// it, whether a late sale or one held up by an SAP outage, goes on the next day's payment. No
    /// invoice gets a payment of its own.
    /// </remarks>
    public string DailyPaymentTimeCAT { get; set; } = "17:00";

    /// <summary>
    /// When (CAT) the vans' daily incoming payments are posted: the partners whose G/L mapping names the
    /// van run.
    /// </summary>
    /// <remarks>
    /// Later than the shops' because van invoices post at 18:00 with a mop-up at 19:30. At 17:00 they would
    /// always be paid a day late.
    /// </remarks>
    public string VanDailyPaymentTimeCAT { get; set; } = "20:00";

    /// <summary>
    /// How often to come back for a daily payment that did not post, such as when SAP was down at
    /// 17:00. Zero turns the retries off, leaving only the 17:00 run.
    /// </summary>
    /// <remarks>
    /// A retry never starts a second payment for a customer who already has one that day. It finishes
    /// the day's payments and creates any that the 17:00 run missed, still counting only invoices posted
    /// before the cut-off.
    /// </remarks>
    public int DailyPaymentRetryMinutes { get; set; } = 10;

    /// <summary>
    /// How far back the daily payment looks for invoices still waiting to be settled.
    /// </summary>
    public int DailyPaymentLookbackDays { get; set; } = 7;
}
