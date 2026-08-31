using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The proof-of-delivery dashboard, and the page a POD operator lands on when
/// they sign in (see <see cref="RoleLandingRoutes"/>).
///
/// It serves two readings of the same shape. A POD operator oversees the
/// company-wide upload and compliance picture, including what is still missing
/// proof and who is uploading. Everyone else reaching this page (a driver, a
/// cashier, a rep) gets the same panels reporting their own uploads.
///
/// Built on the Nocturne system shared with the sales-rep dashboard —
/// wwwroot/css/pod-dashboard.css, linked from App.razor, carries the light and
/// dark palettes and follows the app's theme toggle.
/// </summary>
public partial class PodDashboard
{
    [Inject] private IPodService PodService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<PodDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>
    /// The windows the segmented control offers. Seven days is the default
    /// because it is the span an operator is actually chasing — anything older
    /// has stopped being today's work and belongs in the POD report.
    /// </summary>
    private static readonly int[] RangeOptions = [7, 14, 30];

    /// <summary>Oldest first, so the top of the chase list is the worst of it.</summary>
    private const int OutstandingShown = 8;

    private const int UploaderRanking = 5;

    private int rangeDays = 7;
    private int loadVersion;
    private bool isLoading = true;
    private bool uploadsFailed;
    private bool complianceFailed;
    private DateTime? readAt;

    private bool isOperator;
    private string? username;

    private PodDashboardModel? uploads;
    private Coverage? coverage;
    private IReadOnlyList<PodUploadStatusItem> outstanding = [];
    private IReadOnlyList<Uploader> uploaders = [];
    private int uploaderCount;
    private IReadOnlyList<ChartBar> bars = [];
    private int axisMax;

    private static DateTime Today => DateTime.Today;

    private DateTime RangeStart => Today.AddDays(-(rangeDays - 1));

    /// <summary>What the compliance panels are counting, said in one phrase.</summary>
    private const string ScopeNote = "All locations";

    private string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    /// <summary>
    /// The account's first name. Usernames are email addresses on most accounts,
    /// so the local part is split and its first word taken.
    /// </summary>
    private string Greeted
    {
        get
        {
            var local = (username ?? string.Empty).Split('@')[0];
            var first = local
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(first)) return "there";

            return first.Length == 1
                ? first.ToUpperInvariant()
                : char.ToUpperInvariant(first[0]) + first[1..];
        }
    }

    private string Kicker => isOperator
        ? "POD control · All locations"
        : $"Proof of delivery · {Today:dddd dd MMMM}";

    private string Standfirst
    {
        get
        {
            if (coverage is null)
            {
                return isLoading
                    ? "Reading the uploads and the deliveries still waiting on proof…"
                    : "Uploads, compliance and the deliveries still waiting on proof, in one place.";
            }

            if (coverage.Total == 0)
            {
                return $"No invoices were raised anywhere in the last {rangeDays} days.";
            }

            if (coverage.Outstanding == 0)
            {
                return $"Every one of the last {rangeDays} days' {coverage.Total:N0} deliveries has its proof.";
            }

            var clause = coverage.OutstandingOverADay > 0
                ? $", {coverage.OutstandingOverADay:N0} of them more than 24 hours old"
                : string.Empty;

            return $"{coverage.Outstanding:N0} of {coverage.Total:N0} deliveries are still missing proof{clause}.";
        }
    }

    private string TodayLabel => isOperator ? "Uploads today" : "Your uploads today";

    private string CoverageNote => coverage is null
        ? isLoading ? "Loading…" : "Unavailable"
        : $"{coverage.Uploaded:N0} of {coverage.Total:N0} invoices";

    private string OutstandingNote => coverage switch
    {
        null => isLoading ? "Loading…" : "Unavailable",
        { Outstanding: 0 } => "Nothing waiting on proof",
        { OutstandingOverADay: > 0 } value => $"{value.OutstandingOverADay:N0} over 24h",
        _ => "All raised today"
    };

    private string UploaderNote => uploaderCount switch
    {
        0 => isLoading ? "Loading…" : $"Nobody uploaded in {rangeDays} days",
        1 => $"One person, last {rangeDays} days",
        _ => $"Last {rangeDays} days"
    };

    /// <summary>Held back until the window has been read, so it does not show a zero that is about to move.</summary>
    private int? UploaderFigure => isLoading && uploaderCount == 0 ? null : uploaderCount;

    private string CoveredNote =>
        uploads is { } totals ? $"{totals.TotalUploads:N0} files uploaded" : "—";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The shell paints first and the panels fill in: the compliance window
        // reaches SAP, and an operator should not watch a blank page while it
        // does.
        if (!firstRender) return;

        if (AuthTask is not null)
        {
            var user = (await AuthTask).User;
            username = user.Identity?.Name;
            isOperator = user.IsInRole(UserRoles.PodOperator);
            canOpenCratePods = CratePodRoles.Any(user.IsInRole);
        }

        await LoadAsync();

        await AuditService.LogAsync(AuditActions.ViewDashboard, "PodDashboard", null);
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
        uploadsFailed = complianceFailed = false;
        await InvokeAsync(StateHasChanged);

        // Each loader owns its own panels and its own failure flag, so the
        // compliance window being slow or unavailable never blanks the upload
        // figures, which come from the local database.
        await Task.WhenAll(
            LoadUploadsAsync(version),
            LoadComplianceAsync(version));

        if (version != loadVersion) return;

        isLoading = false;
        readAt = DateTime.Now;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// The upload figures and the activity series. The API scopes these itself:
    /// a POD operator gets all uploads, everybody else their own.
    /// </summary>
    private async Task LoadUploadsAsync(int version)
    {
        try
        {
            var model = await PodService.GetPodDashboardAsync();

            if (version != loadVersion) return;

            if (model is null)
            {
                uploadsFailed = true;
                return;
            }

            uploads = model;
            BuildBars(model.DailyUploads);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;

            Logger.LogWarning(ex, "Failed to load the POD upload figures for the POD dashboard");
            uploadsFailed = true;
        }
        finally
        {
            if (version == loadVersion) await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// The company-wide window's invoices and whether each carries proof.
    /// </summary>
    private async Task LoadComplianceAsync(int version)
    {
        try
        {
            var report = await PodService.GetPodUploadStatusAsync(RangeStart, Today);

            if (version != loadVersion) return;

            if (report is null)
            {
                complianceFailed = true;
                return;
            }

            var items = report.Items;

            BuildCoverage(items);
            BuildOutstanding(items);
            BuildUploaders(items);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;

            Logger.LogWarning(ex, "Failed to load the POD compliance window for the POD dashboard");
            complianceFailed = true;
        }
        finally
        {
            if (version == loadVersion) await InvokeAsync(StateHasChanged);
        }
    }

    private void BuildCoverage(IReadOnlyList<PodUploadStatusItem> items)
    {
        var cutoff = DateTime.Now.AddHours(-24);
        var onTime = 0;
        var overADay = 0;

        foreach (var item in items)
        {
            var raised = ParseDocDate(item.DocDate);

            if (item.HasPod)
            {
                // An upload with no recorded time cannot be shown to have beaten
                // the 24-hour mark, so it counts as a late one.
                if (item.PodUploadedAt is { } uploaded && raised is { } issued
                    && uploaded - issued <= TimeSpan.FromHours(24))
                {
                    onTime++;
                }
            }
            else if (raised is { } stale && stale <= cutoff)
            {
                overADay++;
            }
        }

        coverage = new Coverage(
            items.Count,
            items.Count(item => item.HasPod),
            onTime,
            items.Count(item => !item.HasPod),
            overADay);
    }

    private void BuildOutstanding(IReadOnlyList<PodUploadStatusItem> items)
    {
        outstanding = items
            .Where(item => !item.HasPod)
            .OrderBy(item => ParseDocDate(item.DocDate) ?? DateTime.MaxValue)
            .ThenByDescending(item => item.DocTotal)
            .Take(OutstandingShown)
            .ToList();
    }

    /// <summary>
    /// Who has been uploading across all locations in the selected window.
    /// </summary>
    private void BuildUploaders(IReadOnlyList<PodUploadStatusItem> items)
    {
        var ranked = items
            .SelectMany(item => item.PodUploadedByUsers.Select(user => (Invoice: item.DocEntry, User: user)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.User.Username))
            .GroupBy(entry => entry.User.Username.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Role = group.Select(entry => entry.User.Role).FirstOrDefault(role => !string.IsNullOrWhiteSpace(role)),
                Files = group.Sum(entry => entry.User.FileCount),
                Invoices = group.Select(entry => entry.Invoice).Distinct().Count(),
                Latest = group.Max(entry => entry.User.LatestUploadedAt)
            })
            .OrderByDescending(entry => entry.Files)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        uploaderCount = ranked.Count;

        // Bars read against the leader rather than the group's total, so the
        // ranking stays legible when one driver carries most of the company-wide activity.
        var leader = ranked.Count == 0 ? 0 : ranked[0].Files;

        uploaders = ranked
            .Take(UploaderRanking)
            .Select(entry => new Uploader(
                entry.Name,
                entry.Role,
                entry.Files,
                entry.Invoices,
                entry.Latest,
                leader <= 0 ? "0%" : Percent(entry.Files, leader)))
            .ToList();
    }

    /// <summary>
    /// The activity series, cut to the chosen window. The API always returns the
    /// last 30 days, and its days are UTC — so an upload made in the first two
    /// hours of a CAT morning lands on the previous bar, the same boundary the
    /// "uploads today" figure beside it is counted on.
    /// </summary>
    private void BuildBars(IReadOnlyList<PodDailyCount> daily)
    {
        if (daily.Count == 0)
        {
            bars = [];
            axisMax = 0;
            return;
        }

        var window = daily.Skip(Math.Max(0, daily.Count - rangeDays)).ToList();
        var peak = window.Max(day => day.Count);
        axisMax = NiceCeiling(peak);

        var last = window.Count - 1;
        var built = new List<ChartBar>(window.Count);

        for (var i = 0; i < window.Count; i++)
        {
            var count = window[i].Count;
            var height = axisMax == 0 ? 0m : Math.Round(count / (decimal)axisMax * 100m, 1);

            // A day that had an upload or two but barely would otherwise round
            // away to nothing.
            if (count > 0 && height < 2m) height = 2m;

            var position = last == 0 ? 1d : (double)i / last;
            var quiet = peak > 0 && count < peak * QuietDayShare;

            var band = quiet ? "is-quiet"
                : i == last ? "is-band-4"
                : position >= 0.75d ? "is-band-3"
                : position >= 0.5d ? "is-band-2"
                : string.Empty;

            built.Add(new ChartBar(
                height.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                band,
                $"{window[i].Date} · {count:N0} {(count == 1 ? "upload" : "uploads")}"));
        }

        bars = built;
        chartStart = window[0].Date;
        chartMid = window[window.Count / 2].Date;
    }

    /// <summary>
    /// A day quieter than this share of the window's best drops out of the
    /// recency ramp, so quiet days read as quiet rather than merely old — the
    /// same treatment the sales-rep chart gives its weekends.
    /// </summary>
    private const double QuietDayShare = 0.38d;

    /// <summary>The chart's own day labels, as the API formatted them.</summary>
    private string chartStart = string.Empty;

    private string chartMid = string.Empty;

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private static string Percent(int part, int whole) =>
        whole <= 0 ? "0%" : $"{Math.Round(part / (decimal)whole * 100m, 2).ToString("0.##", CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// Document value in its own currency. Most are USD and read with a symbol;
    /// anything else is named, because a bare figure in the wrong currency is
    /// worse than no figure.
    /// </summary>
    private static string Money(decimal value, string? currency)
    {
        var code = currency?.Trim();

        return string.IsNullOrEmpty(code) || string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase)
            ? $"${value:N0}"
            : $"{code.ToUpperInvariant()} {value:N0}";
    }

    /// <summary>The next tidy whole number at or above the window's best day.</summary>
    private static int NiceCeiling(int value)
    {
        if (value <= 0) return 0;
        if (value <= 5) return value;

        var magnitude = (int)Math.Pow(10, Math.Floor(Math.Log10(value)));

        foreach (var step in new[] { 1, 1.5, 2, 3, 4, 5, 6, 8, 10 })
        {
            var candidate = (int)Math.Ceiling(step * magnitude);
            if (candidate >= value) return candidate;
        }

        return magnitude * 10;
    }

    /// <summary>How long ago something recorded in UTC happened.</summary>
    private static string AgeFromUtc(DateTime moment) => Elapsed(DateTime.UtcNow - moment);

    /// <summary>
    /// How long an invoice has been waiting. Document dates carry no time, so
    /// this is measured in whole days from today.
    /// </summary>
    private static string WaitingFor(string? docDate)
    {
        var days = DaysWaiting(docDate);
        if (days is null) return "—";

        return days switch
        {
            <= 0 => "Today",
            1 => "Yest.",
            _ => $"{days}d"
        };
    }

    /// <summary>
    /// A delivery still without proof two days on has stopped being today's
    /// work and is marked as overdue.
    /// </summary>
    private static string WaitingTone(string? docDate) =>
        DaysWaiting(docDate) >= 2 ? "is-risk" : string.Empty;

    private static string RaisedOn(string? docDate) =>
        ParseDocDate(docDate) is { } raised ? raised.ToString("dd MMM") : "—";

    private static int? DaysWaiting(string? docDate) =>
        ParseDocDate(docDate) is { } raised ? (Today - raised.Date).Days : null;

    private static string Elapsed(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}m",
        { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h",
        { TotalDays: < 2 } => "Yest.",
        { TotalDays: < 30 } => $"{(int)elapsed.TotalDays}d",
        _ => $"{(int)(elapsed.TotalDays / 7)}w"
    };

    /// <summary>The report writes its invoice dates as plain yyyy-MM-dd strings.</summary>
    private static DateTime? ParseDocDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string InvoiceLabel(PodUploadStatusItem item) =>
        item.DocNum > 0 ? item.DocNum.ToString(CultureInfo.InvariantCulture) : $"#{item.DocEntry}";

    private static string CustomerLabel(PodUploadStatusItem item) =>
        string.IsNullOrWhiteSpace(item.CardName) ? item.CardCode ?? "—" : item.CardName!;

    /// <summary>
    /// Opening the POD page against the invoice number puts the operator on the
    /// upload form for that delivery, which is the whole point of the row.
    /// </summary>
    private static string ChaseHref(PodUploadStatusItem item) =>
        item.DocNum > 0 ? $"/pods?docNum={item.DocNum}" : "/pods";

    private static string FileTone(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "is-pdf",
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" => "is-image",
        _ => string.Empty
    };

    private static string FileIcon(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "ph-file-pdf",
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" => "ph-image",
        _ => "ph-file"
    };

    /// <summary>
    /// The three modules this role works through, in the order the work runs.
    /// The last one differs by role: an operator manages the driver accounts
    /// that do the uploading, and everyone else who can reach crate PODs is
    /// pointed at those instead.
    /// </summary>
    private IEnumerable<WorkflowStep> Steps
    {
        get
        {
            yield return new WorkflowStep(
                "1",
                "Collect the proof",
                isOperator
                    ? "Upload signed delivery notes against invoices from any location as they come back in."
                    : "Upload the signed delivery note against the invoice it belongs to.",
                "/pods",
                "Open Product PODs");

            yield return new WorkflowStep(
                "2",
                "Chase what is missing",
                "Work the outstanding list by age, and export it when it has to leave the building.",
                "/pod-report",
                "Open POD Report");

            if (isOperator)
            {
                yield return new WorkflowStep(
                    "3",
                    "Keep the drivers current",
                    "Create and manage the driver accounts that upload from the road.",
                    "/user-management",
                    "Open Driver Accounts");
            }
            else if (canOpenCratePods)
            {
                yield return new WorkflowStep(
                    "3",
                    "Close the crate loop",
                    "Record the crates that came back with the delivery.",
                    "/crates/pods",
                    "Open Crate PODs");
            }
        }
    }

    /// <summary>
    /// Crate PODs are open to every POD-facing role except the cashier, so the
    /// third workflow card is withheld rather than pointing somewhere the user
    /// would be refused. The list mirrors the [Authorize] roles on CratePods.
    /// </summary>
    private bool canOpenCratePods;

    private static readonly string[] CratePodRoles =
    [
        UserRoles.Admin,
        UserRoles.Manager,
        UserRoles.Merchandiser,
        UserRoles.PodOperator,
        UserRoles.Driver,
        UserRoles.SalesRep,
        "Operator"
    ];

    private sealed record ChartBar(string Height, string BandClass, string Label);

    private sealed record Uploader(
        string Name,
        string? Role,
        int Files,
        int Invoices,
        DateTime? Latest,
        string Width);

    private sealed record WorkflowStep(string Number, string Title, string Body, string Href, string Cta);

    private sealed record Coverage(int Total, int Uploaded, int OnTime, int Outstanding, int OutstandingOverADay)
    {
        public decimal Percent => Total <= 0 ? 0m : Uploaded / (decimal)Total * 100m;

        public decimal OnTimePercent => Total <= 0 ? 0m : OnTime / (decimal)Total * 100m;

        /// <summary>Uploaded, but not inside the first 24 hours.</summary>
        public int Late => Math.Max(0, Uploaded - OnTime);
    }
}
