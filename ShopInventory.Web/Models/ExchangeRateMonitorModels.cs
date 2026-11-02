namespace ShopInventory.Web.Models;

// Hand mirror of ShopInventory/DTOs/ExchangeRateMonitorDto.cs. Keep nullability identical: a field
// that is nullable there and not here fails the whole response, and the page reads it as "no data".

public class ExchangeRateMonitorStatus
{
    public string Currency { get; set; } = string.Empty;
    public string? LocalCurrency { get; set; }
    public bool? IndirectQuotation { get; set; }
    public DateOnly Today { get; set; }
    public decimal? TodayRate { get; set; }
    public string? TodayRateText { get; set; }
    public decimal? LocalPerForeign { get; set; }
    public decimal? ForeignPerLocal { get; set; }
    public DateOnly? KeyedThrough { get; set; }
    public bool KeyedBeyondWindow { get; set; }
    public int? DaysKeyedAhead { get; set; }
    public string Health { get; set; } = "Unknown";
    public string HealthReason { get; set; } = string.Empty;
    public DateTime? LastCheckedAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public string? LastError { get; set; }
    public int CheckIntervalMinutes { get; set; }
    public List<ExchangeRateObservation> Observations { get; set; } = [];
    public List<ExchangeRateChange> Changes { get; set; } = [];
    public List<ExchangeRateMonitorAlert> RecentAlerts { get; set; } = [];
    public List<ExchangeRatePriceListFactor> PriceListFactors { get; set; } = [];
}

public class ExchangeRateObservation
{
    public DateOnly RateDate { get; set; }
    public decimal Rate { get; set; }
    public decimal? ReplacedRate { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? SupersededAtUtc { get; set; }
}

public class ExchangeRateChange
{
    public DateOnly RateDate { get; set; }
    public decimal PreviousRate { get; set; }
    public decimal Rate { get; set; }
    public decimal PercentChange { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
}

public class ExchangeRateMonitorAlert
{
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public DateOnly? RateDate { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime RaisedAtUtc { get; set; }
}

public class ExchangeRatePriceListFactor
{
    public int PriceListNo { get; set; }
    public string Name { get; set; } = string.Empty;
    public int BasePriceList { get; set; }
    public string? BasePriceListName { get; set; }
    public decimal Factor { get; set; }
}

public class ExchangeRateMonitorCheckResult
{
    public string Currency { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public int AlertsRaised { get; set; }
}
