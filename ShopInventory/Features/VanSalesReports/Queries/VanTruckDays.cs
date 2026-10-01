using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>One truck on one trading day: the unit every van call rate is measured on.</summary>
/// <param name="Truck">The van account both reps on the truck sign in as, upper-cased.</param>
public readonly record struct VanTruckDayKey(string Truck, DateTime TradingDate);

/// <summary>
/// The calls, plans and sales of each truck-day, pooled across the reps who worked it.
/// </summary>
/// <remarks>
/// <para>
/// Each truck carries two reps under one van account, and they take turns at the counter through the
/// day. One may check in at a shop and the other write its invoice. Measured per rep-day, that single
/// call becomes a check-in that bought nothing on one rep and a sale with no call on the other, and a
/// plan both reps snapshotted at Start Day is counted twice. So the CCR and the PCR are measured per
/// truck-day. Check-ins and buyers are each distinct shops across both reps, and the plan is the
/// truck's, read once.
/// </para>
/// <para>
/// The truck is the rep's <c>AssignedBusinessPartnerCode</c>. A rep with none is treated as a truck
/// of one.
/// </para>
/// <para>
/// A row that covers some rep-days is measured over the truck-days those rep-days touch, in full,
/// including the other rep's half. A rep's strike rate is therefore the truck's on the days they
/// worked, and both reps on a truck show the same figure: the work cannot be split between them.
/// </para>
/// </remarks>
public sealed class VanTruckDays
{
    private readonly Func<Guid, string> _truckOf;
    private readonly Dictionary<VanTruckDayKey, TruckDay> _days = [];

    private VanTruckDays(Func<Guid, string> truckOf) => _truckOf = truckOf;

    /// <summary>One truck-day's pooled figures.</summary>
    public sealed class TruckDay
    {
        public HashSet<string> CheckedInto { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<VanSaleFact> Sales { get; } = [];

        /// <summary>The departure records the truck's reps opened, earliest departure first.</summary>
        public List<VanRouteDayEntity> RouteDays { get; } = [];

        /// <summary>
        /// The truck's plan: the largest any rep on it snapshotted at Start Day, so a plan both reps
        /// recorded counts once. Null when no rep on the truck opened the day.
        /// </summary>
        public int? Planned { get; set; }

        public bool HasCheckIns => CheckedInto.Count > 0;

        /// <summary>
        /// Distinct shops that bought from either rep, with every unattributed sale on the truck-day
        /// one call between them. Not <see cref="VanSalesMeasures.CountProductiveCalls"/>, which
        /// counts per rep-day and would score a shop both reps sold to as two calls that bought.
        /// </summary>
        public int ProductiveCalls => BoughtCodes.Count + (HasUnattributedSale ? 1 : 0);

        /// <summary>Check-ins plus buyers who were not checked into. See <see cref="VanSalesMeasures.CountCallsMade"/>.</summary>
        public int CallsMade => VanSalesMeasures.CountCallsMade(CheckedInto, BoughtCodes, HasUnattributedSale);

        private HashSet<string> BoughtCodes =>
            Sales.Where(sale => sale.RouteCustomerCode is not null)
                .Select(sale => sale.RouteCustomerCode!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private bool HasUnattributedSale => Sales.Any(sale => sale.RouteCustomerCode is null);
    }

    /// <summary>
    /// Builds the truck-days from everything a report has loaded. <c>accounts</c> is each rep's
    /// assigned van account; a rep missing from it is a truck of one.
    /// </summary>
    public static VanTruckDays Build(
        IEnumerable<VanSaleFact> sales,
        IReadOnlyDictionary<VanSalesDayKey, HashSet<string>> visits,
        IEnumerable<VanRouteDayEntity> routeDays,
        IReadOnlyDictionary<Guid, string?> accounts)
    {
        var ledger = new VanTruckDays(userId => TruckOf(userId, accounts));

        foreach (var sale in sales)
        {
            ledger.Day(sale.Key).Sales.Add(sale);
        }

        foreach (var (key, shops) in visits)
        {
            ledger.Day(key).CheckedInto.UnionWith(shops);
        }

        foreach (var day in routeDays)
        {
            var truckDay = ledger.Day(new VanSalesDayKey(day.UserId, day.TradingDate.Date));
            truckDay.Planned = Math.Max(truckDay.Planned ?? 0, day.PlannedCustomerCount);
            truckDay.RouteDays.Add(day);
        }

        foreach (var day in ledger._days.Values)
        {
            day.RouteDays.Sort((a, b) => a.DepartedAt.CompareTo(b.DepartedAt));
        }

        return ledger;
    }

    /// <summary>
    /// Loads the truck-days for a period. Each input a report already holds for the whole fleet can be
    /// passed in and is reused. Pass null for anything the report loaded filtered to a rep or a route:
    /// a filtered load holds one rep's half of a truck, so that input is read again for every rep.
    /// </summary>
    public static async Task<VanTruckDays> LoadAsync(
        ApplicationDbContext db,
        DateTime from,
        DateTime to,
        IReadOnlyCollection<VanSaleFact>? fleetSales,
        IReadOnlyDictionary<VanSalesDayKey, HashSet<string>>? fleetVisits,
        IReadOnlyCollection<VanRouteDayEntity>? fleetRouteDays,
        CancellationToken cancellationToken)
    {
        var sales = fleetSales
                    ?? await VanSalesFactReader.LoadSalesAsync(db, new VanSalesFactFilter(from, to), cancellationToken);
        var visits = fleetVisits ?? await LoadVisitsAsync(db, from, to, cancellationToken);
        var routeDays = fleetRouteDays
                        ?? await db.VanRouteDays
                            .AsNoTracking()
                            .Where(day => day.TradingDate >= from && day.TradingDate <= to)
                            .ToListAsync(cancellationToken);

        var userIds = sales.Select(sale => sale.UserId)
            .Concat(visits.Keys.Select(key => key.UserId))
            .Concat(routeDays.Select(day => day.UserId))
            .Distinct()
            .ToList();

        var accounts = userIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await db.Users
                .AsNoTracking()
                .Where(user => userIds.Contains(user.Id))
                .Select(user => new { user.Id, user.AssignedBusinessPartnerCode })
                .ToDictionaryAsync(user => user.Id, user => user.AssignedBusinessPartnerCode, cancellationToken);

        return Build(sales, visits, routeDays, accounts);
    }

    /// <summary>Every van check-in in the period, as distinct shops per rep per trading day.</summary>
    private static async Task<Dictionary<VanSalesDayKey, HashSet<string>>> LoadVisitsAsync(
        ApplicationDbContext db,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var entries = await db.TimesheetEntries
            .AsNoTracking()
            .Where(entry => entry.Channel == TimesheetChannel.VanSales
                            && entry.CheckInTime >= windowStartUtc
                            && entry.CheckInTime < windowEndUtc)
            .Select(entry => new { entry.UserId, entry.CheckInTime, entry.CustomerCode })
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(entry => new VanSalesDayKey(entry.UserId, VanSalesFacts.TradingDayOf(entry.CheckInTime)))
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.CustomerCode).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The truck a rep works on: their van account, or the rep alone when they have none.</summary>
    public static string TruckOf(Guid userId, IReadOnlyDictionary<Guid, string?> accounts) =>
        accounts.TryGetValue(userId, out var account) && !string.IsNullOrWhiteSpace(account)
            ? account.Trim().ToUpperInvariant()
            : $"rep:{userId:N}";

    public VanTruckDayKey KeyOf(VanSalesDayKey repDay) => new(_truckOf(repDay.UserId), repDay.TradingDate);

    /// <summary>The truck-day a rep-day belongs to, or null when nothing at all was recorded on it.</summary>
    public TruckDay? Find(VanSalesDayKey repDay) =>
        _days.TryGetValue(KeyOf(repDay), out var day) ? day : null;

    /// <summary>
    /// The departure record a rep-day ran under: the rep's own, or failing that the one the other rep
    /// on the truck opened. A Start Day describes the truck's round, so a rep who sold from the truck
    /// without tapping it was still on that route. Null only when nobody on the truck opened the day.
    /// </summary>
    public VanRouteDayEntity? RouteDayOf(VanSalesDayKey repDay)
    {
        var routeDays = Find(repDay)?.RouteDays;

        return routeDays is null or { Count: 0 }
            ? null
            : routeDays.FirstOrDefault(day => day.UserId == repDay.UserId) ?? routeDays[0];
    }

    /// <summary>
    /// <see cref="RouteDayOf"/> for each rep-day that has one, keyed as the reports key their own
    /// departure records.
    /// </summary>
    public Dictionary<VanSalesDayKey, VanRouteDayEntity> RouteDaysFor(IEnumerable<VanSalesDayKey> repDays) =>
        repDays
            .Distinct()
            .Select(key => (Key: key, Day: RouteDayOf(key)))
            .Where(pair => pair.Day is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Day!);

    /// <summary>
    /// Distance covered, once per truck-day. Two reps on a truck may both read the odometer, and
    /// adding their readings would double the trip, so each truck-day takes its longest reading.
    /// Null when no truck-day read both ends. See <see cref="VanSalesMeasures.SumKilometres"/>.
    /// </summary>
    public int? SumKilometres(IEnumerable<VanSalesDayKey> repDays)
    {
        var distances = TruckDaysOf(repDays)
            .Select(day => day.RouteDays
                .Select(routeDay => VanSalesMeasures.SumKilometres([routeDay]))
                .Max())
            .Where(distance => distance.HasValue)
            .Select(distance => distance!.Value)
            .ToList();

        return distances.Count > 0 ? distances.Sum() : null;
    }

    /// <summary>
    /// Distinct shops checked into, summed over the truck-days the rep-days touch. Null when none of
    /// them has a check-in: calls never recorded are not calls never made.
    /// </summary>
    public int? CountCalls(IEnumerable<VanSalesDayKey> repDays)
    {
        var withCalls = TruckDaysOf(repDays).Where(day => day.HasCheckIns).ToList();
        return withCalls.Count == 0 ? null : withCalls.Sum(day => day.CheckedInto.Count);
    }

    /// <summary>The truck's plan, summed over the truck-days touched. Null when no rep opened any of them.</summary>
    public int? SumPlanned(IEnumerable<VanSalesDayKey> repDays)
    {
        var opened = TruckDaysOf(repDays).Where(day => day.Planned.HasValue).ToList();
        return opened.Count == 0 ? null : opened.Sum(day => day.Planned!.Value);
    }

    /// <summary>
    /// The CCR's two halves over the truck-days with a plan above zero, the only days that state one.
    /// Null when there are none.
    /// </summary>
    public (int Planned, int Calls)? MeasureAgainstPlan(IEnumerable<VanSalesDayKey> repDays)
    {
        var planned = TruckDaysOf(repDays).Where(day => day.Planned is > 0).ToList();

        return planned.Count == 0
            ? null
            : (planned.Sum(day => day.Planned!.Value), planned.Sum(day => day.CheckedInto.Count));
    }

    /// <summary>
    /// The PCR's two halves over the truck-days that have check-ins. Null when none does. Both halves
    /// come from the same truck-days, so the rate cannot pass 100%.
    /// </summary>
    public VanSalesMeasures.ProductiveCallBasis? MeasureProductiveCalls(IEnumerable<VanSalesDayKey> repDays)
    {
        var measured = TruckDaysOf(repDays).Where(day => day.HasCheckIns).ToList();

        return measured.Count == 0
            ? null
            : new VanSalesMeasures.ProductiveCallBasis(
                measured.Sum(day => day.ProductiveCalls),
                measured.Sum(day => day.CallsMade));
    }

    private IEnumerable<TruckDay> TruckDaysOf(IEnumerable<VanSalesDayKey> repDays) =>
        repDays
            .Select(KeyOf)
            .Distinct()
            .Select(key => _days.TryGetValue(key, out var day) ? day : null)
            .Where(day => day is not null)
            .Select(day => day!);

    private TruckDay Day(VanSalesDayKey repDay)
    {
        var key = KeyOf(repDay);

        if (!_days.TryGetValue(key, out var day))
        {
            day = new TruckDay();
            _days[key] = day;
        }

        return day;
    }
}
