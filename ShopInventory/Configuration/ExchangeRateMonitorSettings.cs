namespace ShopInventory.Configuration;

/// <summary>
/// The SAP exchange-rate monitor. See <see cref="Services.ExchangeRateMonitor"/>.
/// </summary>
public sealed class ExchangeRateMonitorSettings
{
    public const string SectionName = "ExchangeRateMonitor";

    /// <summary>On by default: ZiG sales orders post to SAP at SAP's rate.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SAP currency codes to watch; empty means ZiG. Empty by default because the configuration
    /// binder appends to a list that already has items, so a default of ["ZiG"] plus a configured
    /// ["ZiG"] would read as two.
    /// </summary>
    public List<string> Currencies { get; set; } = [];

    /// <summary>The currencies to watch, as SAP codes, each once.</summary>
    public IReadOnlyList<string> MonitoredCurrencies()
    {
        var codes = Currencies
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => Common.Sales.SapCurrencyCodes.ToSapCode(code.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return codes.Count > 0 ? codes : [Common.Sales.SapCurrencyCodes.Zig];
    }

    /// <summary>How often SAP is asked. Each pass is one call per day in the window.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>How many days after today are read. A change keyed ahead is reported when it is keyed.</summary>
    public int LookAheadDays { get; set; } = 31;

    /// <summary>
    /// On the first successful pass, how many days back are read into the history, so the page has
    /// one from the start. Never alerted on.
    /// </summary>
    public int HistoryBackfillDays { get; set; } = 120;

    /// <summary>
    /// Warn when the keyed rates run out within this many days. From the first unkeyed day SAP
    /// refuses every document in the currency dated that day.
    /// </summary>
    public int RunningOutWarningDays { get; set; } = 7;

    /// <summary>
    /// A change at least this large (percent) is flagged as one to check for a typing slip. On five
    /// month-ends in 2025 production's ZiG rate went from 36 to 50 for one day and back.
    /// </summary>
    public decimal LargeChangePercent { get; set; } = 10m;

    /// <summary>
    /// Who is emailed. Empty falls back to <c>SystemHealthAlert:AlertRecipients</c>. The bell is
    /// always used.
    /// </summary>
    public List<string> AlertRecipients { get; set; } = [];
}
