using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The SalesRep landing page, rendered by <see cref="Home"/> in place of the
/// cashier dashboard. Built on the imported Nocturne design (Sales Rep
/// Dashboard.dc.html); see the note at the top of the markup for what the
/// import contributes and what the app shell already carries.
/// </summary>
public partial class SalesRepDashboard
{
    [Inject] private ISalesOrderService SalesOrderService { get; set; } = default!;
    [Inject] private IPodService PodService { get; set; } = default!;
    [Inject] private ITimesheetService TimesheetService { get; set; } = default!;
    [Inject] private ILogger<SalesRepDashboard> Logger { get; set; } = default!;

    /// <summary>
    /// The rep's first name, already derived by Home — this page never shows the
    /// raw username, which is an email address for most accounts.
    /// </summary>
    [Parameter] public string DisplayName { get; set; } = string.Empty;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>
    /// The account name behind <see cref="DisplayName"/>. A first name cannot
    /// filter an API, so the one call that has to be scoped to this rep — the
    /// week's visits — reads the authenticated identity instead of being handed
    /// the name to display.
    /// </summary>
    private string? username;

    /// <summary>
    /// The ranges the segmented control offers. 14 days is the import's own
    /// default and the one the chart was drawn at.
    /// </summary>
    private static readonly int[] RangeOptions = [7, 14, 30];

    /// <summary>
    /// One page of orders per window. A rep-scale fortnight sits far inside
    /// this, but the aggregates say so when it does not — see
    /// <see cref="ordersTruncated"/>.
    /// </summary>
    private const int OrderFetchSize = 500;

    /// <summary>Enough of the review queue to name its oldest entry.</summary>
    private const int MobileFetchSize = 200;

    /// <summary>A week of one rep's visits fits well inside this.</summary>
    private const int VisitFetchSize = 200;

    /// <summary>Signed percentage, with the sign written into every section.</summary>
    private const string PercentFormat = "+0.0'%';-0.0'%';0'%'";

    /// <summary>
    /// A day whose takings fall below this share of the window's best day drops
    /// out of the recency ramp, so quiet days read as quiet rather than merely
    /// old. Taken from the import, where the two weekend bars and one public
    /// holiday sit at the dimmest step.
    /// </summary>
    private const decimal QuietDayShare = 0.38m;

    private int rangeDays = 14;
    private int loadVersion;
    private bool isLoading = true;
    private string? loadError;
    private bool ordersFailed;
    private bool mobileFailed;
    private bool podFailed;
    private bool visitsFailed;

    private int? ordersToday;
    private int ordersYesterday;
    private bool hasOrders;
    private bool ordersTruncated;
    private int rangeOrderCount;
    private decimal rangeValue;
    private decimal? valueChangePercent;
    private decimal axisMax;
    private IReadOnlyList<ChartBar> bars = [];
    private IReadOnlyList<SalesOrderDto> recentOrders = [];
    private IReadOnlyList<TopCustomer> topCustomers = [];

    private int? mobilePending;
    private DateTime? oldestMobile;

    private PodCoverage? podCoverage;

    private int? openVisits;

    private static DateTime Today => DateTime.Today;

    private DateTime RangeStart => Today.AddDays(-(rangeDays - 1));

    private static string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    private string Greeted => string.IsNullOrWhiteSpace(DisplayName) ? "there" : DisplayName;

    private string Standfirst
    {
        get
        {
            if (mobilePending is null && podCoverage is null)
            {
                return isLoading
                    ? "Pulling your orders, the review queue and the delivery proof together…"
                    : "Your sales orders, the mobile review queue and the delivery-proof follow-up, in one place.";
            }

            var clauses = new List<string>();

            if (mobilePending is int waiting && waiting > 0)
            {
                clauses.Add($"{waiting:N0} mobile {(waiting == 1 ? "order needs" : "orders need")} a decision");
            }

            if (podCoverage is { Outstanding: > 0 } coverage)
            {
                clauses.Add(
                    $"{coverage.Outstanding:N0} {(coverage.Outstanding == 1 ? "delivery is" : "deliveries are")} still missing proof");
            }

            return clauses.Count == 0
                ? "Nothing is waiting on you: the review queue is clear and every delivery has its proof."
                : string.Join(" and ", clauses) + ".";
        }
    }

    private string MobileQueueNote => mobilePending switch
    {
        null => isLoading ? "Loading…" : "Unavailable",
        0 => "Nothing waiting on review",
        _ when oldestMobile is { } oldest => $"Oldest waiting {Age(oldest)}",
        var count => $"{count:N0} waiting on review"
    };

    private string PodQueueNote => podCoverage switch
    {
        null => isLoading ? "Loading…" : "Unavailable",
        { Outstanding: 0 } => "Every delivery has its proof",
        { OutstandingOverADay: > 0 } coverage => $"{coverage.OutstandingOverADay:N0} over 24h",
        _ => $"Raised in the last {rangeDays} days"
    };

    private string TimesheetQueueNote => openVisits switch
    {
        null => isLoading ? "Loading…" : "Unavailable",
        0 => $"Week ending {WeekEnd:dd MMM}",
        _ => $"Since {WeekStart:dd MMM}, still open"
    };

    /// <summary>Monday of the current week, the boundary Timesheets reports on.</summary>
    private static DateTime WeekStart => Today.AddDays(-(((int)Today.DayOfWeek + 6) % 7));

    private static DateTime WeekEnd => WeekStart.AddDays(6);

    private static readonly WorkflowStep[] Steps =
    [
        new("1",
            "Review incoming orders",
            "Track status and update order details without crossing into invoicing.",
            "/sales-orders",
            "Open Sales Orders"),
        new("2",
            "Process mobile submissions",
            "Validate what the field captured and move it into the sales workflow.",
            "/mobile-drafts",
            "Open Mobile Orders"),
        new("3",
            "Close the delivery loop",
            "Upload proof of delivery and chase the documents still outstanding.",
            "/pod-dashboard",
            "Open POD Dashboard")
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The shell paints first and the panels fill in: the order window can
        // reach SAP, and a rep should not watch a blank page while it does.
        if (!firstRender) return;

        if (AuthTask is not null)
        {
            username = (await AuthTask).User.Identity?.Name;
        }

        await LoadAsync();
    }

    private async Task SelectRange(int days)
    {
        if (days == rangeDays || isLoading) return;

        rangeDays = days;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var version = ++loadVersion;
        isLoading = true;
        loadError = null;
        ordersFailed = mobileFailed = podFailed = visitsFailed = false;
        await InvokeAsync(StateHasChanged);

        // Each loader owns its own panel and its own failure flag, so one slow
        // or unavailable source never holds up or blanks the others.
        await Task.WhenAll(
            LoadOrdersAsync(version),
            LoadMobileQueueAsync(version),
            LoadPodCoverageAsync(version),
            LoadOpenVisitsAsync(version));

        if (version != loadVersion) return;

        List<string> failed = [];
        if (ordersFailed) failed.Add("sales orders");
        if (mobileFailed) failed.Add("the mobile queue");
        if (podFailed) failed.Add("delivery proof");
        if (visitsFailed) failed.Add("timesheets");

        loadError = failed.Count == 0
            ? null
            : $"Some panels could not be loaded ({string.Join(", ", failed)}). Everything else on this page is current.";

        isLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// The window's orders drive the chart, the value figures, the recent queue
    /// and the customer ranking; the window before it gives the comparison. The
    /// two run together because each can reach SAP.
    /// </summary>
    private async Task LoadOrdersAsync(int version)
    {
        var from = RangeStart;
        var previousFrom = from.AddDays(-rangeDays);
        var previousTo = from.AddDays(-1);

        try
        {
            var currentTask = SalesOrderService.GetSalesOrdersAsync(1, OrderFetchSize, fromDate: from, toDate: Today);
            var previousTask = SalesOrderService.GetSalesOrdersAsync(1, OrderFetchSize, fromDate: previousFrom, toDate: previousTo);
            await Task.WhenAll(currentTask, previousTask);

            if (version != loadVersion) return;

            var current = await currentTask;
            var previous = await previousTask;

            if (current is null)
            {
                ordersFailed = true;
                return;
            }

            ApplyOrders(current, from);
            ApplyComparison(previous);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;

            Logger.LogWarning(ex, "Failed to load the sales-order window for the sales rep dashboard");
            ordersFailed = true;
        }
        finally
        {
            if (version == loadVersion) await InvokeAsync(StateHasChanged);
        }
    }

    private void ApplyOrders(SalesOrderListResponse response, DateTime from)
    {
        // Unsynced local orders are returned ahead of the SAP page rather than
        // merged into its date order, so the recent queue has to sort before it
        // takes a top slice.
        var fetched = response.Orders
            .OrderByDescending(order => order.OrderDate)
            .ThenByDescending(order => order.Id)
            .ToList();

        // Van sales accounts are dropped before anything is counted, so every
        // panel on this page reads the same book of business the rep is
        // measured on.
        var orders = fetched.Where(IsRepBusiness).ToList();

        // Truncation is judged on what the fetch returned, since that is what
        // the page size cut short; the count then reports what survived the
        // exclusion rather than the server's unfiltered total.
        ordersTruncated = fetched.Count < response.TotalCount;
        rangeOrderCount = orders.Count;
        hasOrders = true;
        recentOrders = orders.Take(6).ToList();

        ordersToday = orders.Count(order => order.OrderDate.Date == Today);
        ordersYesterday = orders.Count(order => order.OrderDate.Date == Today.AddDays(-1));

        // DocTotal is summed as it stands, the same reading Home gives the
        // cashier dashboard's invoice and payment totals.
        var daily = new decimal[rangeDays];
        var dailyCount = new int[rangeDays];
        foreach (var order in orders)
        {
            var day = (order.OrderDate.Date - from).Days;
            if (day < 0 || day >= rangeDays) continue;

            daily[day] += order.DocTotal;
            dailyCount[day]++;
        }

        rangeValue = daily.Sum();
        BuildBars(daily, dailyCount, from);
        BuildTopCustomers(orders);
    }

    private void ApplyComparison(SalesOrderListResponse? previous)
    {
        // A window the fetch could not cover in full would be compared against a
        // window it did, so the change is withheld rather than understated.
        if (previous is null || previous.Orders.Count < previous.TotalCount || ordersTruncated)
        {
            valueChangePercent = null;
            return;
        }

        var previousValue = previous.Orders.Where(IsRepBusiness).Sum(order => order.DocTotal);
        valueChangePercent = previousValue == 0
            ? null
            : Math.Round((rangeValue - previousValue) / previousValue * 100m, 1);
    }

    private void BuildBars(decimal[] daily, int[] dailyCount, DateTime from)
    {
        var peak = daily.Length == 0 ? 0m : daily.Max();
        axisMax = NiceCeiling(peak);

        var built = new List<ChartBar>(daily.Length);
        var last = daily.Length - 1;

        for (var i = 0; i < daily.Length; i++)
        {
            var value = daily[i];
            var height = axisMax == 0 ? 0m : Math.Round(value / axisMax * 100m, 1);

            // A day that traded but barely would otherwise round away to nothing.
            if (value > 0 && height < 2m) height = 2m;

            var position = last == 0 ? 1d : (double)i / last;
            var quiet = peak > 0 && value < peak * QuietDayShare;

            var band = quiet ? "is-quiet"
                : i == last ? "is-band-4"
                : position >= 0.75d ? "is-band-3"
                : position >= 0.5d ? "is-band-2"
                : string.Empty;

            var date = from.AddDays(i);
            var count = dailyCount[i];
            var note = count switch
            {
                0 => "No orders",
                1 => "1 order",
                _ => $"{count} orders"
            };

            // The tooltip sits above the bar, so a tall one would carry it off
            // the top of the plot; those flip it inside the bar instead. The
            // columns at either end pin it to their own edge for the same
            // reason, since a centred card would hang past the card's side.
            var tip = height >= 62m ? "is-inside" : string.Empty;
            if (i <= 1) tip = $"{tip} is-start".Trim();
            else if (i >= last - 1) tip = $"{tip} is-end".Trim();

            built.Add(new ChartBar(
                height.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                band,
                tip,
                date.ToString("ddd dd MMM", CultureInfo.CurrentCulture),
                Money(value),
                note,
                $"{date:ddd dd MMM}: {Money(value)}, {note}"));
        }

        bars = built;
    }

    private void BuildTopCustomers(IEnumerable<SalesOrderDto> orders)
    {
        var ranked = orders
            .GroupBy(order => CustomerLabel(order), StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.Key, Value = group.Sum(order => order.DocTotal), Count = group.Count() })
            .Where(entry => entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .Take(5)
            .ToList();

        // Bars are read against the leader rather than the window's total, so
        // the ranking stays legible when one account dominates it.
        var leader = ranked.Count == 0 ? 0m : ranked[0].Value;

        topCustomers = ranked
            .Select((entry, index) =>
            {
                // The row shows an abbreviated total against an ellipsised name,
                // so the tooltip is where the exact figure, the full name and
                // the account's share of the window are read.
                var orderNote = entry.Count == 1 ? "1 order" : $"{entry.Count} orders";
                var note = rangeValue <= 0
                    ? orderNote
                    : $"{orderNote} · {Math.Round(entry.Value / rangeValue * 100m, 1).ToString("0.#", CultureInfo.InvariantCulture)}% of the window";

                return new TopCustomer(
                    entry.Name,
                    entry.Value,
                    leader == 0 ? "0%" : Math.Round(entry.Value / leader * 100m, 1).ToString("0.#", CultureInfo.InvariantCulture) + "%",
                    // The leader's tooltip would sit over the card's own heading,
                    // so the top row alone drops its card below the bar.
                    index == 0 ? "is-row is-below" : "is-row",
                    Money(entry.Value),
                    note,
                    $"{entry.Name}: {Money(entry.Value)}, {note}");
            })
            .ToList();
    }

    /// <summary>
    /// The mobile review queue. This counts orders the field submitted and
    /// nobody has acted on yet; Mobile Orders shows a larger "pending" figure
    /// because it also folds in orders approved here that have not reached SAP,
    /// which are a sync problem rather than a decision waiting to be made.
    /// </summary>
    private async Task LoadMobileQueueAsync(int version)
    {
        try
        {
            var response = await SalesOrderService.GetSalesOrdersAsync(
                1,
                MobileFetchSize,
                status: SalesOrderStatus.Pending,
                source: SalesOrderSource.Mobile);

            if (version != loadVersion) return;

            if (response is null)
            {
                mobileFailed = true;
                return;
            }

            // Van sales accounts are dropped here as everywhere else on this
            // page, which the queue can only be counted net of once the fetch
            // covers all of it. A queue deeper than one page keeps the server's
            // total, which is a decision count either way.
            var covered = response.Orders.Count >= response.TotalCount;
            var queue = response.Orders.Where(IsRepBusiness).ToList();

            mobilePending = covered ? queue.Count : response.TotalCount;

            // The list arrives newest first, so the oldest entry is only really
            // the oldest when the page covers the whole queue.
            oldestMobile = covered && queue.Count > 0
                ? queue.Min(order => OrderMoment(order))
                : null;
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;

            Logger.LogWarning(ex, "Failed to load the mobile review queue for the sales rep dashboard");
            mobileFailed = true;
        }
        finally
        {
            if (version == loadVersion) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadPodCoverageAsync(int version)
    {
        try
        {
            var report = await PodService.GetPodUploadStatusAsync(RangeStart, Today);

            if (version != loadVersion) return;

            if (report is null)
            {
                podFailed = true;
                return;
            }

            var cutoff = DateTime.Now.AddHours(-24);
            var onTime = 0;
            var outstandingOverADay = 0;

            foreach (var item in report.Items)
            {
                var raised = ParseDocDate(item.DocDate);

                if (item.HasPod)
                {
                    // An upload with no recorded time cannot be shown to have
                    // beaten the 24-hour mark, so it counts as a late one.
                    if (item.PodUploadedAt is { } uploaded && raised is { } issued
                        && uploaded - issued <= TimeSpan.FromHours(24))
                    {
                        onTime++;
                    }
                }
                else if (raised is { } stale && stale <= cutoff)
                {
                    outstandingOverADay++;
                }
            }

            podCoverage = new PodCoverage(
                report.TotalInvoices,
                report.UploadedCount,
                onTime,
                report.PendingCount,
                outstandingOverADay);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;

            Logger.LogWarning(ex, "Failed to load the POD upload status for the sales rep dashboard");
            podFailed = true;
        }
        finally
        {
            if (version == loadVersion) await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Visits this rep checked into this week and never checked out of. The
    /// timesheet API only scopes itself to the caller for merchandisers, so the
    /// username is passed explicitly.
    /// </summary>
    private async Task LoadOpenVisitsAsync(int version)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            openVisits = null;
            return;
        }

        try
        {
            var response = await TimesheetService.GetTimesheetsAsync(
                1,
                VisitFetchSize,
                username: username,
                fromDate: WeekStart,
                toDate: Today.AddDays(1).AddSeconds(-1));

            if (version != loadVersion) return;

            if (response is null)
            {
                visitsFailed = true;
                return;
            }

            openVisits = response.Entries.Count(entry => entry.CheckOutTime is null);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;

            Logger.LogWarning(ex, "Failed to load open timesheet visits for the sales rep dashboard");
            visitsFailed = true;
        }
        finally
        {
            if (version == loadVersion) await InvokeAsync(StateHasChanged);
        }
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    /// <summary>
    /// Money at figure size: thousands and millions are abbreviated so a KPI
    /// keeps its one line. Totals are read in the document currency as it
    /// stands, matching the rest of the app's dashboards.
    /// </summary>
    private static string Compact(decimal value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        var magnitude = Math.Abs(value);

        return magnitude switch
        {
            >= 1_000_000m => $"{sign}${magnitude / 1_000_000m:0.#}M",
            >= 1_000m => $"{sign}${magnitude / 1_000m:0.#}k",
            _ => $"{sign}${magnitude:0}"
        };
    }

    private static string Money(decimal value) => $"${value:N0}";

    private static string Percent(int part, int whole) =>
        whole <= 0 ? "0%" : $"{Math.Round(part / (decimal)whole * 100m, 2).ToString("0.##", CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// The next tidy number at or above the window's best day, with a little
    /// headroom so the tallest bar stops short of the top gridline. The set is
    /// chosen so the thirds the axis labels sit on stay readable.
    /// </summary>
    private static decimal NiceCeiling(decimal value)
    {
        if (value <= 0) return 0m;

        var target = value * 1.08m;
        var magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)target)));

        foreach (var step in AxisSteps)
        {
            var candidate = step * magnitude;
            if (candidate >= target) return candidate;
        }

        return magnitude * 10m;
    }

    private static readonly decimal[] AxisSteps = [1m, 1.5m, 2m, 3m, 4m, 5m, 6m, 8m, 10m];

    private static string Age(DateTime moment)
    {
        var elapsed = DateTime.Now - moment;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "now",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}m",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h",
            { TotalDays: < 2 } => "Yest.",
            _ => $"{(int)elapsed.TotalDays}d"
        };
    }

    /// <summary>
    /// When the order landed. Locally captured orders carry a timestamp; orders
    /// read back from SAP carry only a date, so the age of those is measured
    /// from the order date.
    /// </summary>
    private static DateTime OrderMoment(SalesOrderDto order) =>
        order.CreatedAt == default ? order.OrderDate : order.CreatedAt;

    private static string OrderLabel(SalesOrderDto order) =>
        order.SAPDocNum is > 0 ? $"SO-{order.SAPDocNum}" : order.OrderNumber;

    /// <summary>
    /// Whether the order counts towards a rep. Van sales accounts move stock
    /// onto the vans rather than out to a customer any rep covers, and they
    /// trade at a scale that would otherwise own the value chart and every
    /// place in the customer ranking.
    /// </summary>
    private static bool IsRepBusiness(SalesOrderDto order) =>
        !VanSalesAccounts.IsVanSalesAccount(order.CardCode);

    private static string CustomerLabel(SalesOrderDto order) =>
        string.IsNullOrWhiteSpace(order.CardName) ? order.CardCode : order.CardName!;

    private static string ChannelLabel(SalesOrderDto order) =>
        order.Source == SalesOrderSource.Mobile ? "Mobile" : "Desktop";

    /// <summary>
    /// Only an unsynced draft or pending order has an edit page to open — the
    /// same guard Sales Orders puts on its own edit action. Everything else
    /// hands off to the list.
    /// </summary>
    private static string OrderHref(SalesOrderDto order) =>
        order.Status is SalesOrderStatus.Draft or SalesOrderStatus.Pending && !order.IsSynced
            ? $"/sales-orders/edit/{order.Id}"
            : "/sales-orders";

    /// <summary>
    /// Three tones, as the import draws them: work that has landed, work still
    /// in the queue, and work that has gone wrong.
    /// </summary>
    private static string StatusTone(SalesOrderStatus status) => status switch
    {
        SalesOrderStatus.Approved or SalesOrderStatus.PartiallyFulfilled or SalesOrderStatus.Fulfilled => "is-ok",
        SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected or SalesOrderStatus.OnHold => "is-risk",
        _ => string.Empty
    };

    private static string StatusLabel(SalesOrderStatus status) => status switch
    {
        SalesOrderStatus.Draft => "Draft",
        SalesOrderStatus.Pending => "To review",
        SalesOrderStatus.Approved => "Confirmed",
        SalesOrderStatus.PartiallyFulfilled => "Part delivered",
        SalesOrderStatus.Fulfilled => "Delivered",
        SalesOrderStatus.Cancelled => "Cancelled",
        SalesOrderStatus.OnHold => "On hold",
        SalesOrderStatus.Rejected => "Rejected",
        _ => status.ToString()
    };

    /// <summary>The report writes its invoice dates as plain yyyy-MM-dd strings.</summary>
    private static DateTime? ParseDocDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private sealed record ChartBar(
        string Height,
        string BandClass,
        string TipClass,
        string Day,
        string Value,
        string Note,
        string Label);

    private sealed record TopCustomer(
        string Name,
        decimal Value,
        string Width,
        string TipClass,
        string Exact,
        string Note,
        string Label);

    private sealed record WorkflowStep(string Number, string Title, string Body, string Href, string Cta);

    private sealed record PodCoverage(int Total, int Uploaded, int OnTime, int Outstanding, int OutstandingOverADay)
    {
        public decimal Percent => Total <= 0 ? 0m : Uploaded / (decimal)Total * 100m;

        public decimal OnTimePercent => Total <= 0 ? 0m : OnTime / (decimal)Total * 100m;

        public int Late => Math.Max(0, Uploaded - OnTime);
    }
}
