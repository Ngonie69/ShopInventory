namespace ShopInventory.Web.Data;

/// <summary>
/// Supported POD report email send frequencies.
/// </summary>
public enum PodReportEmailFrequency
{
    Weekly,
    Monthly,
    Daily,
    EveryNDays
}

/// <summary>
/// A single POD report email schedule rule: one frequency + send time + its own recipient list.
/// Multiple schedules can be configured, each delivering to different recipients.
/// </summary>
public class PodReportEmailSchedule
{
    public int Id { get; set; }

    /// <summary>
    /// Human-friendly name shown in the settings UI (e.g. "Ops - weekly").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this schedule participates in automatic scheduled sending.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the report is sent. Stored as the <see cref="PodReportEmailFrequency"/> name.
    /// </summary>
    public string Frequency { get; set; } = nameof(PodReportEmailFrequency.Weekly);

    /// <summary>
    /// Day of week for <see cref="PodReportEmailFrequency.Weekly"/> schedules (0 = Sunday), in local (CAT) time.
    /// </summary>
    public int? DayOfWeek { get; set; }

    /// <summary>
    /// Day of month (1-31) for <see cref="PodReportEmailFrequency.Monthly"/> schedules.
    /// </summary>
    public int? DayOfMonth { get; set; }

    /// <summary>
    /// Interval in days for <see cref="PodReportEmailFrequency.EveryNDays"/> schedules.
    /// </summary>
    public int? IntervalDays { get; set; }

    /// <summary>
    /// Time of day the report is sent, as a minute-of-day offset (0-1439) in the business
    /// timezone (CAT). 0 = 00:00, 390 = 06:30. Stored local — never UTC — so the wall-clock
    /// send time is independent of the server's timezone.
    /// </summary>
    public int SendMinuteOfDay { get; set; }

    /// <summary>
    /// To recipients (comma/semicolon/newline separated, same format as the email service expects).
    /// </summary>
    public string ToRecipients { get; set; } = string.Empty;

    /// <summary>
    /// Cc recipients (comma/semicolon/newline separated).
    /// </summary>
    public string CcRecipients { get; set; } = string.Empty;

    /// <summary>
    /// When this schedule last sent successfully (UTC). Null until the first send.
    /// </summary>
    public DateTime? LastSentUtc { get; set; }

    /// <summary>
    /// Anchor date used to compute Daily/EveryNDays cadence before the first send (UTC).
    /// </summary>
    public DateTime AnchorDateUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTime LastModifiedAtUtc { get; set; } = DateTime.UtcNow;

    public string? LastModifiedBy { get; set; }
}
