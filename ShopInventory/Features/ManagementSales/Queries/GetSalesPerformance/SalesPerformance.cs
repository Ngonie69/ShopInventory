namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

/// <summary>
/// How every line of business traded over a period, set beside the period before: the shops, vending and
/// the vans read together, for the management email.
/// </summary>
/// <remarks><list type="table">
/// <item><term>Scorecard</term><description>One row per line of business and currency, in the order shops, vending, vans. Currencies are never added together.</description></item>
/// <item><term>Totals</term><description>Each currency's takings across all three lines of business. Money may add across channels; products may not, so no item ranking spans them.</description></item>
/// <item><term>Shops</term><description>The shop tills, from the Shop &amp; Vending review. Null when no shop sold in the period or the one before.</description></item>
/// <item><term>Vending</term><description>The vending settlements, from the same review. Null like <c>Shops</c>.</description></item>
/// <item><term>Vans</term><description>The vans, from the van sales performance report — the reservations and the offline uploads together.</description></item>
/// </list></remarks>
public sealed record SalesPerformance(
    DateTime FromDate,
    DateTime ToDate,
    DateTime PreviousFromDate,
    DateTime PreviousToDate,
    DateTime GeneratedAtUtc,
    List<SalesPerformanceScore> Scorecard,
    List<SalesPerformanceTotal> Totals,
    SalesPerformanceCounter? Shops,
    SalesPerformanceCounter? Vending,
    SalesPerformanceVans Vans);

/// <summary>One line of business in one currency, against the period before.</summary>
/// <remarks><list type="table">
/// <item><term>Takings</term><description>Tax-inclusive, as rung up or invoiced. Shop and vending credits are stated beside it in <c>Credited</c>, never taken off it.</description></item>
/// <item><term>Transactions</term><description>Till sales and vending settlements for the counters; invoices for the vans.</description></item>
/// <item><term>Credited</term><description>What has been credited back on the period's sales. Null for the vans, whose report does not read credits.</description></item>
/// <item><term>SharePercent</term><description>This line's share of the currency's takings across all three.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceScore(
    string Business,
    string Label,
    string Currency,
    decimal Takings,
    decimal PreviousTakings,
    decimal? ChangePercent,
    int Transactions,
    int PreviousTransactions,
    decimal? AverageTransaction,
    decimal? Credited,
    decimal SharePercent);

/// <summary>One currency's takings across every line of business.</summary>
public sealed record SalesPerformanceTotal(
    string Currency,
    decimal Takings,
    decimal PreviousTakings,
    decimal? ChangePercent,
    int Transactions);

/// <summary>A counter business — shops or vending — as the Shop &amp; Vending review read it.</summary>
/// <remarks><list type="table">
/// <item><term>Findings</term><description>The review's own findings for it, most urgent first. Written there from the figures, never here.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceCounter(
    string Business,
    string Label,
    List<SalesPerformanceCounterCurrency> Currencies,
    List<SalesPerformanceFinding> Findings);

/// <summary>A counter business in one currency.</summary>
/// <remarks><list type="table">
/// <item><term>MarginPercent</term><description>SAP's gross profit over the revenue it costed; null until SAP has costed any of it.</description></item>
/// <item><term>Outlets</term><description>Each shop or vending depot, biggest first.</description></item>
/// <item><term>TopItems</term><description>Its best sellers by value before VAT, for this business alone.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceCounterCurrency(
    string Currency,
    decimal Takings,
    decimal NetOfCredits,
    int Transactions,
    decimal AverageTransaction,
    int DaysTraded,
    decimal? MarginPercent,
    List<SalesPerformanceOutlet> Outlets,
    List<SalesPerformanceItem> TopItems);

/// <summary>One shop or depot.</summary>
/// <remarks><list type="table">
/// <item><term>PerTradingDay</term><description>Takings over the days it sold anything: the fair comparison between outlets that traded different numbers of days.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceOutlet(
    string Label,
    string Channel,
    int DaysTraded,
    decimal Takings,
    decimal PerTradingDay,
    decimal? ChangePercent,
    decimal SharePercent,
    decimal? MarginPercent);

/// <summary>One best seller, before VAT.</summary>
public sealed record SalesPerformanceItem(
    string ItemCode,
    string? Description,
    decimal Quantity,
    decimal NetAmount,
    decimal SharePercent,
    decimal? ChangePercent);

/// <summary>Something worth a manager's attention.</summary>
/// <remarks><list type="table">
/// <item><term>Severity</term><description><c>action</c>, <c>review</c> or <c>note</c>, as the Shop &amp; Vending review grades them.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceFinding(
    string Severity,
    string Title,
    string Detail);

/// <summary>The vans over the period, against the period before.</summary>
/// <remarks><list type="table">
/// <item><term>Currencies</term><description>Takings per currency, tax-inclusive on both the online and offline routes.</description></item>
/// <item><term>MainCurrency</term><description>The currency the vans took most in; route changes and item values are measured in it.</description></item>
/// <item><term>TruckDays</term><description>Rep-days that traded. Two reps share a truck, so this is not a count of trucks.</description></item>
/// <item><term>StrikeRate</term><description>Calls that bought over calls made, pooled per truck-day; null with no visit records.</description></item>
/// <item><term>Routes</term><description>Each route, biggest first; sales whose rep opened no day come last as "No route day".</description></item>
/// <item><term>Reps</term><description>The rep league, biggest takings first.</description></item>
/// <item><term>UnconfirmedSales</term><description>Van reservations in the period that expired rather than confirmed. A sale made while SAP was down ends this way and drops out of every van figure.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceVans(
    List<SalesPerformanceVanCurrency> Currencies,
    string MainCurrency,
    int Invoices,
    int PreviousInvoices,
    int RepCount,
    int RouteCount,
    int TruckDays,
    int? Calls,
    int ProductiveCalls,
    double? StrikeRate,
    double? PreviousStrikeRate,
    int OutletsThatBought,
    int NewOutlets,
    int? KilometresTravelled,
    List<SalesPerformanceRoute> Routes,
    List<SalesPerformanceRep> Reps,
    List<SalesPerformanceVanItem> TopItems,
    int UnconfirmedSales,
    List<SalesPerformanceFinding> Findings);

/// <summary>Van takings in one currency.</summary>
public sealed record SalesPerformanceVanCurrency(
    string Currency,
    decimal Takings,
    decimal PreviousTakings,
    decimal? ChangePercent,
    int Invoices,
    decimal? AverageDrop);

/// <summary>One van route.</summary>
/// <remarks><list type="table">
/// <item><term>Takings</term><description>Per currency, keyed by currency code.</description></item>
/// <item><term>ChangePercent</term><description>Against the same route's takings in the main currency in the period before; null when it did not trade then.</description></item>
/// <item><term>CallCompliance</term><description>Planned calls made over calls planned; null when no truck-day supplied a plan.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceRoute(
    string Label,
    string? Territory,
    bool HasRouteDay,
    int TradingDays,
    Dictionary<string, decimal> Takings,
    decimal? ChangePercent,
    int ProductiveCalls,
    double? CallCompliance,
    double? StrikeRate,
    int? KilometresTravelled);

/// <summary>
/// One van item, ranked as the van report ranks them: on reach — the shops that bought it — because van
/// lines record no unit, so quantities cannot be compared across items.
/// </summary>
/// <remarks><list type="table">
/// <item><term>Takings</term><description>Tax-inclusive line value in the vans' main currency.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceVanItem(
    string ItemCode,
    string? Description,
    int OutletsThatBought,
    int Lines,
    decimal Takings);

/// <summary>One rep.</summary>
public sealed record SalesPerformanceRep(
    string Name,
    List<string> Routes,
    int TradingDays,
    Dictionary<string, decimal> Takings,
    int ProductiveCalls,
    int OutletsThatBought,
    double? StrikeRate);
