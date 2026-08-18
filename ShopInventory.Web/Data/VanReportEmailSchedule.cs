namespace ShopInventory.Web.Data;

/// <summary>
/// Which van sales report a schedule sends.
/// </summary>
/// <remarks>
/// Stored as the member name rather than the integer, so inserting a report in the middle of this
/// list cannot silently re-point every existing schedule at a different report.
/// </remarks>
public enum VanReportKind
{
    Scorecard,
    Exceptions,
    Margin,
    Coverage,
    Performance,
    Stock,
    Replenishment
}

/// <summary>
/// One recurring van sales report email: which report, how often, to whom.
/// </summary>
/// <remarks>
/// A sibling of <see cref="PodReportEmailSchedule"/> rather than a column added to it. The two
/// answer different questions and are edited by different people, and widening the POD table would
/// have put van settings on the POD screen. What they genuinely share — deciding when a schedule is
/// due — lives in <c>ReportScheduleCadence</c> and is used by both, so the calendar arithmetic has
/// one definition even though the tables do not.
///
/// The reporting window is derived from the frequency rather than stored: a daily send covers
/// yesterday, a weekly one the last seven days, a monthly one the last thirty. Storing it would let
/// somebody configure a weekly email that reports on a single day and wonder why the totals looked
/// wrong.
/// </remarks>
public class VanReportEmailSchedule
{
    public int Id { get; set; }

    /// <summary>Human-friendly name shown in the settings UI (for example "Ops — weekly scorecard").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this schedule participates in automatic sending. The master toggle gates all of them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which report to send. Stored as the <see cref="VanReportKind"/> name.</summary>
    public string ReportKind { get; set; } = nameof(VanReportKind.Scorecard);

    /// <summary>How often. Stored as the <c>ReportScheduleFrequency</c> name.</summary>
    public string Frequency { get; set; } = nameof(ShopInventory.Web.Services.ReportScheduleFrequency.Weekly);

    /// <summary>Day of week for weekly schedules (0 = Sunday), in local CAT time.</summary>
    public int? DayOfWeek { get; set; }

    /// <summary>Day of month (1-31) for monthly, quarterly and half-yearly schedules.</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Interval in days for every-N-days schedules.</summary>
    public int? IntervalDays { get; set; }

    /// <summary>
    /// Time of day the report is sent, as a minute-of-day offset (0-1439) in the business timezone
    /// (CAT). Stored local — never UTC — so the wall-clock send time is independent of the server's
    /// timezone.
    /// </summary>
    public int SendMinuteOfDay { get; set; } = 6 * 60;

    /// <summary>To recipients, in the same separated format the email service expects.</summary>
    public string ToRecipients { get; set; } = string.Empty;

    /// <summary>Cc recipients.</summary>
    public string CcRecipients { get; set; } = string.Empty;

    /// <summary>When this schedule last sent successfully (UTC). Null until the first send.</summary>
    public DateTime? LastSentUtc { get; set; }

    /// <summary>
    /// Why the last attempt failed, or null where the last attempt succeeded.
    /// </summary>
    /// <remarks>
    /// Kept on the row rather than only in the log, because a schedule that has silently stopped
    /// arriving is the failure nobody reports — the recipients assume there was nothing to send.
    /// </remarks>
    public string? LastError { get; set; }

    /// <summary>
    /// Anchor used to compute cadence before the first send (UTC), and the floor that stops a
    /// newly-saved schedule firing immediately for a period it was never meant to cover.
    /// </summary>
    public DateTime AnchorDateUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTime LastModifiedAtUtc { get; set; } = DateTime.UtcNow;

    public string? LastModifiedBy { get; set; }
}
