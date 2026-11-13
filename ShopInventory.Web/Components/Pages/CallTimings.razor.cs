using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Features.CallTimings.Queries.GetCallTimings;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The reading behind /call-timings: the API's own timings of its inbound requests and its SAP calls.
/// </summary>
/// <remarks>
/// <para>
/// Built so the next read that gridlocks the SAP slots is seen while it happens. Each of the stuck-read
/// incidents of November 2026 was pieced together afterwards from log lines and the Service Layer's
/// access logs; the API itself kept no timing of a single call. Here the reads in flight and their age,
/// the family SAP is spending its time on and the slot waits are on one screen.
/// </para>
/// <para>
/// Live re-reads every <see cref="LiveSeconds"/> seconds. The endpoint reads only the API's memory, so
/// that costs SAP nothing. The loop starts after the first interactive render, so the prerender never
/// starts one, and stops on dispose.
/// </para>
/// </remarks>
public partial class CallTimings
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    private const int LiveSeconds = 15;

    /// <summary>The API's origin for a SAP call made outside any inbound request (<c>ApiCallContext.Background</c>).</summary>
    private const string BackgroundOrigin = "(background)";

    /// <summary>Rows shown in a long table before "Show all".</summary>
    private const int CollapsedRows = 15;

    private static readonly NocturneSelectOption<string>[] WindowOptions =
    [
        new("today", "Today") { IsUnset = true },
        new("1h", "Last hour"),
        new("15m", "Last 15 minutes"),
    ];

    private static readonly NocturneSelectOption<string>[] RouteSortOptions =
    [
        new("calls", "Requests") { IsUnset = true },
        new("p95", "p95"),
        new("time", "Total time"),
        new("sap", "SAP time"),
    ];

    private readonly CancellationTokenSource disposed = new();

    private bool isLoading = true;
    private bool isRefreshing;
    private bool live = true;
    private bool showAllFamilies;
    private bool showAllRoutes;
    private string window = "today";
    private string routeSort = "calls";

    private string? loadError;
    private DateTime? loadedAtUtc;
    private CallTimingsModel? timings;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        isLoading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _ = LiveLoopAsync(disposed.Token);

        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var username = authState.User.Identity?.Name ?? "Unknown";
        var role = authState.User.Claims
            .FirstOrDefault(claim => claim.Type == System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";
        await AuditService.LogAsync(
            "ViewCallTimings", username, role, "System", null,
            "Accessed the API call timings page", "/call-timings");
    }

    private async Task LiveLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(LiveSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!live || isRefreshing)
                {
                    continue;
                }

                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
    }

    private async Task LoadAsync()
    {
        var result = await Mediator.Send(new GetCallTimingsQuery(window), disposed.Token);
        if (result.IsError)
        {
            loadError = result.FirstError.Description;
        }
        else
        {
            timings = result.Value;
            loadError = null;
        }

        loadedAtUtc = DateTime.UtcNow;
    }

    private async Task RefreshAsync()
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        try
        {
            await LoadAsync();
        }
        catch (OperationCanceledException) when (disposed.IsCancellationRequested)
        {
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private void ToggleLive(ChangeEventArgs args) => live = args.Value is true;

    public void Dispose()
    {
        disposed.Cancel();
        disposed.Dispose();
    }

    // ---- What the page reads -----------------------------------------------------------------

    private IEnumerable<SapFamilyTimingModel> VisibleFamilies =>
        showAllFamilies ? timings!.Families : timings!.Families.Take(CollapsedRows);

    private IEnumerable<InboundRouteTimingModel> VisibleRoutes
    {
        get
        {
            IEnumerable<InboundRouteTimingModel> sorted = routeSort switch
            {
                "p95" => timings!.Routes.OrderByDescending(route => route.P95Ms),
                "time" => timings!.Routes.OrderByDescending(route => route.TotalSeconds),
                "sap" => timings!.Routes.OrderByDescending(route => route.SapSeconds),
                _ => timings!.Routes,
            };
            return showAllRoutes ? sorted : sorted.Take(CollapsedRows);
        }
    }

    private int Failures => timings!.Families.Sum(family => family.Timeouts + family.GaveUp);

    private int PresumedStuck => timings!.Families.Sum(family => family.PresumedStuck);

    private int ServerErrors => timings!.Callers.Sum(caller => caller.ServerErrors);

    /// <summary>From when the figures run, which is later than the window when the API restarted inside it.</summary>
    private string CoverageText
    {
        get
        {
            var start = timings!.SapCoverageStartUtc > timings.InboundCoverageStartUtc
                ? timings.SapCoverageStartUtc
                : timings.InboundCoverageStartUtc;
            var later = start > timings.WindowStartUtc.AddSeconds(1);
            return later
                ? $"Figures from {FormatTime(start)} CAT{(timings.RecordingSinceUtc >= start.AddSeconds(-1) ? ", when the API started" : ", the oldest still kept")}"
                : $"Figures from {FormatTime(start)} CAT";
        }
    }

    // ---- Tones ---------------------------------------------------------------------------------

    private string SlotTone => timings!.Slots.Limit > 0 && timings.Slots.InUse >= timings.Slots.Limit ? "bad"
        : timings.Slots.InUse > 0 ? "warn" : "good";

    private string InFlightTone => timings!.InFlight.Count == 0 ? "good"
        : timings.InFlight[0].AtSapMs >= 60_000 ? "bad"
        : timings.InFlight[0].AgeMs >= 15_000 ? "warn" : "good";

    private string FailureTone => PresumedStuck > 0 ? "bad" : Failures > 0 ? "warn" : "good";

    private string WaitTone => timings!.SlotWaits.WaitedOverTwoSeconds > 0 ? "warn" : "good";

    private string InboundTone => ServerErrors > 0 ? "warn" : "good";

    private static string FamilySlotTone(SapReadFamilySlotsModel family) =>
        family.Kept > 0 ? "bad" : family.InUse >= family.Limit ? "warn" : "good";

    private static string StageTone(SapCallInFlightTimingModel call) => call.Stage switch
    {
        "at-sap" when call.AtSapMs >= 60_000 => "bad",
        "at-sap" when call.AtSapMs >= 15_000 => "warn",
        "at-sap" => "good",
        _ => call.AgeMs >= 2_000 ? "warn" : "idle",
    };

    private static string StageText(string stage) => stage switch
    {
        "at-sap" => "At SAP",
        "family-slot" => "Queued for its family",
        "slot" => "Queued for a slot",
        _ => stage,
    };

    private static string LatencyTone(int milliseconds) =>
        milliseconds >= 15_000 ? "bad" : milliseconds >= 2_000 ? "warn" : "plain";

    private static string OutcomeTone(string outcome) => outcome switch
    {
        "ok" => "good",
        "gave-up" => "warn",
        "not-sent" => "idle",
        _ => "bad",
    };

    private static string OutcomeText(SapCallTimingModel call) => call.Outcome switch
    {
        "ok" => $"Answered {call.StatusCode}",
        "error" => call.StatusCode > 0 ? $"Error {call.StatusCode}" : "Connection failed",
        "timeout" => call.StatusCode > 0 ? $"Timed out ({call.StatusCode})" : "Timed out",
        "gave-up" => "Caller stopped waiting",
        "not-sent" => "Not sent",
        _ => call.Outcome,
    };

    // ---- Formatting ----------------------------------------------------------------------------

    /// <summary>"840 ms", "4.2 s", "3 min 12 s".</summary>
    private static string FormatDuration(int milliseconds) => milliseconds switch
    {
        < 1_000 => $"{milliseconds} ms",
        < 60_000 => $"{milliseconds / 1000d:0.0} s",
        _ => $"{milliseconds / 60_000} min {milliseconds % 60_000 / 1000} s",
    };

    private static string FormatSeconds(double seconds) => seconds < 60
        ? $"{seconds:0.0} s"
        : seconds < 3600 ? $"{seconds / 60:0.0} min" : $"{seconds / 3600:0.0} h";

    private static string FormatClock(DateTime? utc) =>
        utc.HasValue ? $"{IAuditService.ToCAT(EnsureUtc(utc.Value)):HH:mm:ss} CAT" : "—";

    private static string FormatTime(DateTime? utc) =>
        utc.HasValue ? $"{IAuditService.ToCAT(EnsureUtc(utc.Value)):HH:mm:ss}" : "—";

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
