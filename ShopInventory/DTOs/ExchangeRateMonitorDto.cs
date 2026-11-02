namespace ShopInventory.DTOs;

/// <summary>
/// Where the SAP rate monitor stands for one currency. Mirrored by hand in
/// <c>ShopInventory.Web/Models/ExchangeRateMonitorModels.cs</c>: keep nullability identical.
/// </summary>
public class ExchangeRateMonitorStatusDto
{
    /// <summary>SAP's code, e.g. ZiG.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>SAP's local currency, e.g. USD. Null until the first successful check.</summary>
    public string? LocalCurrency { get; set; }

    /// <summary>True when SAP's rate means "1 local = rate foreign". Null until the first successful check.</summary>
    public bool? IndirectQuotation { get; set; }

    /// <summary>Today in Harare, the date SAP documents carry.</summary>
    public DateOnly Today { get; set; }

    /// <summary>SAP's rate for today, as SAP holds it. Null when none is keyed or nothing has been read.</summary>
    public decimal? TodayRate { get; set; }

    /// <summary>"1 USD = 30 ZiG", or null.</summary>
    public string? TodayRateText { get; set; }

    /// <summary>USD value of one ZiG at today's rate, or null.</summary>
    public decimal? LocalPerForeign { get; set; }

    /// <summary>ZiG per USD at today's rate, or null.</summary>
    public decimal? ForeignPerLocal { get; set; }

    /// <summary>The last day of the unbroken run of keyed rates from today. Null when today has none.</summary>
    public DateOnly? KeyedThrough { get; set; }

    /// <summary>True when the run reached the end of the look-ahead window, so it may go further.</summary>
    public bool KeyedBeyondWindow { get; set; }

    /// <summary>Days after today that have a rate keyed. Null when today has none.</summary>
    public int? DaysKeyedAhead { get; set; }

    /// <summary>Ok, Warning or Error.</summary>
    public string Health { get; set; } = "Unknown";

    /// <summary>One sentence on why <see cref="Health"/> is what it is.</summary>
    public string HealthReason { get; set; } = string.Empty;

    public DateTime? LastCheckedAtUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    public string? LastError { get; set; }

    public int CheckIntervalMinutes { get; set; }

    /// <summary>Each rate value SAP has held for a day, newest day first, including replaced values.</summary>
    public List<ExchangeRateObservationDto> Observations { get; set; } = [];

    /// <summary>The days the rate differs from the day before, newest first.</summary>
    public List<ExchangeRateChangeDto> Changes { get; set; } = [];

    public List<ExchangeRateMonitorAlertDto> RecentAlerts { get; set; } = [];

    /// <summary>Price lists in this currency that are a fixed multiple of another list.</summary>
    public List<ExchangeRatePriceListFactorDto> PriceListFactors { get; set; } = [];
}

public class ExchangeRateObservationDto
{
    public DateOnly RateDate { get; set; }
    public decimal Rate { get; set; }
    public decimal? ReplacedRate { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? SupersededAtUtc { get; set; }
}

public class ExchangeRateChangeDto
{
    public DateOnly RateDate { get; set; }
    public decimal PreviousRate { get; set; }
    public decimal Rate { get; set; }
    public decimal PercentChange { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
}

public class ExchangeRateMonitorAlertDto
{
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public DateOnly? RateDate { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime RaisedAtUtc { get; set; }
}

public class ExchangeRatePriceListFactorDto
{
    public int PriceListNo { get; set; }
    public string Name { get; set; } = string.Empty;
    public int BasePriceList { get; set; }
    public string? BasePriceListName { get; set; }
    public decimal Factor { get; set; }
}

public class ExchangeRateMonitorCheckResultDto
{
    public string Currency { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public int AlertsRaised { get; set; }
}
