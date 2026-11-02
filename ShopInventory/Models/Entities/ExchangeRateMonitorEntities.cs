using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One value SAP held for a currency's rate on one day, and when the monitor saw it.
/// </summary>
/// <remarks>
/// A row is never edited to a new rate. When SAP's rate for a day changes, the row holding the old
/// value is stamped <see cref="SupersededAtUtc"/> and a new row is added, so the table is the full
/// record of what SAP said and when — including a day someone re-keyed after documents were booked
/// at the old value.
/// </remarks>
public class ExchangeRateObservationEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>SAP's currency code, e.g. <c>ZiG</c>.</summary>
    [Required]
    [MaxLength(10)]
    public string Currency { get; set; } = null!;

    /// <summary>The day the rate is keyed for (a calendar date in SAP, no time).</summary>
    public DateOnly RateDate { get; set; }

    /// <summary>The number exactly as SAP holds it. Its direction is <see cref="IndirectQuotation"/>.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Rate { get; set; }

    /// <summary>
    /// True when <see cref="Rate"/> means "1 local = Rate foreign" (production: 1 USD = 30 ZiG).
    /// </summary>
    public bool IndirectQuotation { get; set; }

    /// <summary>The company's local currency when this was read. Production: USD.</summary>
    [Required]
    [MaxLength(10)]
    public string LocalCurrency { get; set; } = null!;

    /// <summary>
    /// The value this day held before, when SAP's rate for an already-seen day was changed. Null for
    /// the first value seen for the day.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal? ReplacedRate { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>When SAP stopped holding this value for the day. Null while it is still SAP's rate.</summary>
    public DateTime? SupersededAtUtc { get; set; }
}

/// <summary>
/// An alert the rate monitor raised. The unique <see cref="AlertKey"/> is what stops the next pass,
/// on whichever node, from raising the same one again.
/// </summary>
public class ExchangeRateMonitorAlertEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>What makes this alert the same alert next time, e.g. <c>ZiG:change:2026-11-05:30→28</c>.</summary>
    [Required]
    [MaxLength(200)]
    public string AlertKey { get; set; } = null!;

    [Required]
    [MaxLength(10)]
    public string Currency { get; set; } = null!;

    /// <summary>One of <see cref="ExchangeRateMonitorAlertKinds"/>.</summary>
    [Required]
    [MaxLength(40)]
    public string Kind { get; set; } = null!;

    /// <summary>Error, Warning or Info, as the notification bell reads it.</summary>
    [Required]
    [MaxLength(20)]
    public string Severity { get; set; } = null!;

    /// <summary>The rate day the alert is about.</summary>
    public DateOnly? RateDate { get; set; }

    [Required]
    [MaxLength(2000)]
    public string Message { get; set; } = null!;

    public DateTime RaisedAtUtc { get; set; }
}

public static class ExchangeRateMonitorAlertKinds
{
    /// <summary>The rate differs from the day before's.</summary>
    public const string Change = "Change";

    /// <summary>A day's rate was re-keyed in SAP after the monitor had seen it.</summary>
    public const string Edited = "Edited";

    /// <summary>No rate is keyed for today: SAP refuses documents in the currency.</summary>
    public const string MissingToday = "MissingToday";

    /// <summary>The keyed rates run out within the warning window.</summary>
    public const string RunningOut = "RunningOut";

    /// <summary>The company's local currency or quotation direction changed.</summary>
    public const string SettingsChanged = "SettingsChanged";

    /// <summary>The monitor has not been able to read SAP for a while, so it would miss a change.</summary>
    public const string CheckFailing = "CheckFailing";
}

/// <summary>
/// Where the monitor stands for one currency: when it last asked SAP, whether that worked, and how
/// far ahead SAP's rates are keyed.
/// </summary>
public class ExchangeRateMonitorStateEntity
{
    [Key]
    [MaxLength(10)]
    public string Currency { get; set; } = null!;

    public DateTime? LastCheckedAtUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    /// <summary>Why the last check failed, or null when it worked.</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>The last day, from today, with an unbroken run of rates. Null when today has none.</summary>
    public DateOnly? KeyedThrough { get; set; }

    /// <summary>True when the run of keyed days reached the end of the look-ahead window.</summary>
    public bool KeyedBeyondWindow { get; set; }

    [MaxLength(10)]
    public string? LocalCurrency { get; set; }

    public bool? IndirectQuotation { get; set; }

    /// <summary>
    /// The active price lists in this currency whose prices are a fixed multiple of another list's,
    /// as JSON (<c>ExchangeRatePriceListFactorDto[]</c>). They do not follow the exchange rate.
    /// </summary>
    public string? PriceListFactorsJson { get; set; }
}
