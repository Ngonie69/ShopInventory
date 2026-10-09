using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Features.LiveTransactions.Queries.GetLiveTransactionFeed;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// /live-transactions: today's sales, invoices and payments, and every fiscal receipt, failure and fiscal
/// day the Fiscalisation platform records, as they happen.
/// </summary>
/// <remarks>
/// The page holds the whole day's events and derives every count from them, so the figures, the
/// attention lanes and the list can never disagree. It reads the day once on open, then polls every
/// <see cref="PollInterval"/> from a little before the newest event it holds, because a row stamped
/// earlier can commit later; re-reading that overlap is harmless since events are keyed by id. A sale
/// announced over the hub triggers a poll at once rather than waiting for the next tick.
/// </remarks>
public partial class LiveTransactions : IAsyncDisposable
{
    private const int PageLimit = 500;
    private const int MaxPagesPerRefresh = 40;
    private const int MaxHeldEvents = 20_000;
    private const int MaxRows = 250;
    private const int MaxCardsPerLane = 6;
    private const int MaxLocations = 6;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a sale may wait for its receipt before it is a problem rather than a sale in flight. The
    /// platform answers in seconds; ten minutes is a handset out of signal or a stuck queue.
    /// </summary>
    private static readonly TimeSpan WaitingAfter = TimeSpan.FromMinutes(10);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private INotificationHubService HubService { get; set; } = default!;
    [Inject] private IDesktopIntegrationService DesktopService { get; set; } = default!;

    private readonly Dictionary<string, LiveTransactionEventModel> _events = new(StringComparer.Ordinal);

    // The newest fiscal receipt and attempt for each sale or invoice, by that document's event id.
    private readonly Dictionary<string, LiveTransactionEventModel> _receiptFor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveTransactionEventModel> _attemptFor = new(StringComparer.Ordinal);

    // Retries pressed on this page, by the sale's event id. A successful retry counts as fiscalised at
    // once: the receipt it produced reaches the feed on a later poll, if the platform's feed is readable.
    private readonly Dictionary<string, RetryState> _retries = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // What arrived while someone was watching, and until when to mark it. Held for longer than one poll so
    // a quiet poll does not wipe it, and set on a sale or invoice when its fiscal outcome arrives, since
    // that changes a row already on screen.
    private readonly Dictionary<string, DateTime> _freshUntilUtc = new(StringComparer.Ordinal);
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(8);
    private DateTime _tradingDayCat;
    private DateTime? _cursorUtc;
    private bool _caughtUp;
    private bool _isLoading = true;
    private bool _isRefreshing;
    private bool _loadFailed;
    private bool _paused;
    private DateTime? _lastUpdatedUtc;
    private bool _fiscalAvailable = true;
    private string? _fiscalMessage;
    private bool _trimmed;

    private Filter _filter = Filter.All;
    private string _search = string.Empty;
    private string? _openEventId;

    private PeriodicTimer? _pollTimer;
    private Task? _pollTask;

    private enum Filter { All, Sales, Money, Fiscal, Failures }

    private enum Tone { Good, Info, Warn, Bad, Idle }

    private sealed record FiscalState(Tone Tone, string Word, string? Detail);

    private sealed record RetryState(bool Busy, bool Succeeded, string? Message);

    private sealed record FiscalBreakdown(int Fiscalised, int InFlight, int Waiting, int Refused)
    {
        public int Total => Fiscalised + InFlight + Waiting + Refused;
    }

    private sealed record HourBar(int Hour, int Sales, int Failures, bool Current, bool Future);

    private sealed record ChainStep(string Label, string? Sub, Tone Tone);

    private sealed record AttentionCard(
        string Key,
        string Reference,
        string? Amount,
        string? Where,
        string? Reason,
        string State,
        Tone Tone,
        LiveTransactionEventModel? RetrySale);

    private sealed record Lane(string Title, Tone Tone, string EmptyText, List<AttentionCard> Cards);

    private sealed record DeviceRow(int DeviceId, string? Location, int? DayNo, string State, Tone Tone, int Receipts, DateTime? LastReceiptUtc);

    private sealed record LocationRow(string Name, int Count, string Totals, int Percent);

    // ─── Lifecycle ────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        _tradingDayCat = TodayCat();
        await RefreshAsync(initial: true);

        HubService.OnDesktopSaleCreated += OnSaleCreated;
        _pollTimer = new PeriodicTimer(PollInterval);
        _pollTask = PollAsync(_lifetime.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_pollTimer is not null && await _pollTimer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_paused)
                {
                    await InvokeAsync(() => RefreshAsync(initial: false));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSaleCreated()
    {
        if (_paused)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            try
            {
                await RefreshAsync(initial: false);
            }
            catch (Exception)
            {
                // The next tick reads the same events; a failed nudge costs at most one interval.
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        HubService.OnDesktopSaleCreated -= OnSaleCreated;
        _lifetime.Cancel();
        _pollTimer?.Dispose();

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
        _refreshGate.Dispose();
    }

    // ─── Reading the feed ─────────────────────────────────────────────────

    private Task ManualRefreshAsync() => RefreshAsync(initial: false);

    /// <summary>
    /// Pausing stops the polls, not the page: the list holds still for reading, and resuming catches up
    /// from the cursor in one read, so nothing that happened meanwhile is missed.
    /// </summary>
    private async Task TogglePauseAsync()
    {
        _paused = !_paused;
        if (!_paused)
        {
            await RefreshAsync(initial: false);
        }
    }

    private async Task RefreshAsync(bool initial)
    {
        // A tick or a hub nudge that lands while a read is running is dropped, not queued: the running
        // read will already return whatever the nudge was about.
        if (_lifetime.IsCancellationRequested || !await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var today = TodayCat();
            if (today != _tradingDayCat)
            {
                // Midnight CAT: the figures are "today", so the day starts empty again.
                _tradingDayCat = today;
                ResetDay();
                initial = true;
            }

            _isRefreshing = true;
            if (!initial)
            {
                StateHasChanged();
            }

            var succeeded = false;
            var freshUntil = DateTime.UtcNow + FreshFor;

            for (var page = 0; page < MaxPagesPerRefresh; page++)
            {
                DateTime? since = _cursorUtc is { } cursor
                    ? (_caughtUp ? cursor - Overlap : cursor)
                    : null;

                var result = await Mediator.Send(new GetLiveTransactionFeedQuery(since, PageLimit), _lifetime.Token);
                if (result.IsError)
                {
                    _loadFailed = true;
                    break;
                }

                succeeded = true;
                _loadFailed = false;
                var feed = result.Value;
                _fiscalAvailable = feed.FiscalFeedAvailable;
                _fiscalMessage = feed.FiscalFeedMessage;

                foreach (var e in feed.Events)
                {
                    e.OccurredAtUtc = DateTime.SpecifyKind(e.OccurredAtUtc, DateTimeKind.Utc);
                    if (_events.TryAdd(e.EventId, e))
                    {
                        IndexFiscalLink(e);

                        // The day's backlog is not news; only what arrives while the page is open is.
                        if (!initial)
                        {
                            _freshUntilUtc[e.EventId] = freshUntil;
                            if (e.LinkedEventId is { } target)
                            {
                                _freshUntilUtc[target] = freshUntil;
                            }
                        }
                    }
                }

                if (feed.Events.Count > 0)
                {
                    var newest = feed.Events[^1].OccurredAtUtc;
                    _cursorUtc = _cursorUtc is null || newest > _cursorUtc ? newest : _cursorUtc;
                }

                if (!feed.HasMore)
                {
                    _caughtUp = true;
                    break;
                }

                _caughtUp = false;
            }

            if (succeeded)
            {
                _lastUpdatedUtc = DateTime.UtcNow;
            }

            var nowUtc = DateTime.UtcNow;
            foreach (var expired in _freshUntilUtc.Where(f => f.Value <= nowUtc).Select(f => f.Key).ToList())
            {
                _freshUntilUtc.Remove(expired);
            }

            TrimToCap();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _isLoading = false;
            _isRefreshing = false;
            _refreshGate.Release();
        }

        if (!_lifetime.IsCancellationRequested)
        {
            StateHasChanged();
        }
    }

    private void IndexFiscalLink(LiveTransactionEventModel e)
    {
        if (e.LinkedEventId is not { } target)
        {
            return;
        }

        var index = e.Kind switch
        {
            LiveTransactionKinds.FiscalReceipt => _receiptFor,
            LiveTransactionKinds.FiscalAttempt => _attemptFor,
            _ => null
        };

        if (index is null)
        {
            return;
        }

        if (!index.TryGetValue(target, out var held) || held.OccurredAtUtc <= e.OccurredAtUtc)
        {
            index[target] = e;
        }
    }

    private void ResetDay()
    {
        _events.Clear();
        _receiptFor.Clear();
        _attemptFor.Clear();
        _retries.Clear();
        _freshUntilUtc.Clear();
        _cursorUtc = null;
        _caughtUp = false;
        _trimmed = false;
        _openEventId = null;
    }

    /// <summary>
    /// A very busy day is capped rather than left to grow the circuit without bound. The figures then
    /// describe what is held, and the page says so.
    /// </summary>
    private void TrimToCap()
    {
        if (_events.Count <= MaxHeldEvents)
        {
            return;
        }

        foreach (var oldest in _events.Values
                     .OrderBy(e => e.OccurredAtUtc)
                     .Take(_events.Count - MaxHeldEvents)
                     .ToList())
        {
            _events.Remove(oldest.EventId);
        }

        _trimmed = true;
    }

    // ─── Retrying a sale ──────────────────────────────────────────────────

    /// <summary>
    /// Asks the device to sign a refused or stuck till or van sale now. Safe to press twice: the API asks
    /// the device for an existing receipt before submitting, so a repeat adopts the first receipt.
    /// </summary>
    private async Task RetryAsync(LiveTransactionEventModel sale)
    {
        if (sale.Reference is not { } reference
            || (_retries.TryGetValue(sale.EventId, out var running) && running.Busy))
        {
            return;
        }

        _retries[sale.EventId] = new RetryState(Busy: true, Succeeded: false, Message: null);

        try
        {
            var (result, error) = await DesktopService.RetrySaleFiscalisationAsync(reference, _lifetime.Token);
            _retries[sale.EventId] = error is null
                ? new RetryState(false, true, result?.FiscalReceiptNumber is { } receipt ? $"receipt {receipt}" : null)
                : new RetryState(false, false, error);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        await RefreshAsync(initial: false);
    }

    // ─── What the page shows ──────────────────────────────────────────────

    private void SetFilter(Filter filter) => _filter = filter;

    private void ToggleOpen(string eventId) => _openEventId = _openEventId == eventId ? null : eventId;

    private bool IsFresh(string eventId) =>
        _freshUntilUtc.TryGetValue(eventId, out var until) && until > DateTime.UtcNow;

    private IEnumerable<LiveTransactionEventModel> Sales =>
        _events.Values.Where(e => e.Kind == LiveTransactionKinds.Sale);

    private IEnumerable<LiveTransactionEventModel> Invoices =>
        _events.Values.Where(e => e.Kind == LiveTransactionKinds.Invoice);

    private IEnumerable<LiveTransactionEventModel> Payments =>
        _events.Values.Where(e =>
            e.Kind is LiveTransactionKinds.IncomingPayment or LiveTransactionKinds.MobilePayment && !e.IsFailure);

    private List<LiveTransactionEventModel> VisibleRows(out int matching)
    {
        var query = _events.Values.Where(e => Matches(e, _filter));

        if (!string.IsNullOrWhiteSpace(_search))
        {
            var term = _search.Trim();
            query = query.Where(e =>
                Contains(e.Reference, term) || Contains(e.Counterparty, term)
                || Contains(e.Location, term) || Contains(e.Channel, term)
                || Contains(e.FiscalDeviceId?.ToString(CultureInfo.InvariantCulture), term));
        }

        var all = query.ToList();
        matching = all.Count;

        return all
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.EventId, StringComparer.Ordinal)
            .Take(MaxRows)
            .ToList();
    }

    private bool Matches(LiveTransactionEventModel e, Filter filter) => filter switch
    {
        // A fiscal receipt or attempt for a document already on the list is folded into that document's
        // row, not drawn again as a row of its own.
        Filter.All => !IsShownUnderItsDocument(e),
        Filter.Sales => e.Kind == LiveTransactionKinds.Sale,
        Filter.Money => e.Kind is LiveTransactionKinds.Invoice or LiveTransactionKinds.IncomingPayment or LiveTransactionKinds.MobilePayment,
        Filter.Fiscal => LiveTransactionKinds.IsFiscal(e.Kind),
        Filter.Failures => IsOpenFailure(e),
        _ => true
    };

    private bool IsShownUnderItsDocument(LiveTransactionEventModel e) =>
        e.Kind is LiveTransactionKinds.FiscalReceipt or LiveTransactionKinds.FiscalAttempt
        && e.LinkedEventId is { } target
        && _events.ContainsKey(target);

    /// <summary>
    /// A failure nobody has fixed yet. A failed fiscal attempt stops counting once a receipt for the same
    /// document arrives, which is what a successful retry looks like from here.
    /// </summary>
    private bool IsOpenFailure(LiveTransactionEventModel e)
    {
        if (e.Kind is LiveTransactionKinds.Sale or LiveTransactionKinds.Invoice
            && FiscalStateOf(e)?.Tone == Tone.Bad)
        {
            return true;
        }

        if (!e.IsFailure)
        {
            return false;
        }

        if (e.Kind == LiveTransactionKinds.Sale)
        {
            // Failed by the status this system held, but fiscalised since.
            return false;
        }

        if (e.Kind == LiveTransactionKinds.FiscalAttempt && e.LinkedEventId is { } target)
        {
            // Counted once, on the document, when the document is held.
            return !_events.ContainsKey(target) && !_receiptFor.ContainsKey(target);
        }

        if (e.Kind == LiveTransactionKinds.FiscalDay)
        {
            // Only the device's latest word on its day counts; a close that failed then succeeded is fixed.
            return LatestDayEvent(e.FiscalDeviceId)?.EventId == e.EventId;
        }

        return true;
    }

    private int OpenFailureCount => _events.Values.Count(IsOpenFailure);

    /// <summary>
    /// The fiscal state of a sale or invoice: a receipt the platform reported beats anything this system
    /// last wrote down, because this system only learns of it on its next status sync — and a sale's own
    /// status is the one it had when the feed first read it.
    /// </summary>
    private FiscalState? FiscalStateOf(LiveTransactionEventModel e)
    {
        if (e.Kind is not (LiveTransactionKinds.Sale or LiveTransactionKinds.Invoice))
        {
            return null;
        }

        if (_receiptFor.TryGetValue(e.EventId, out var receipt))
        {
            var number = receipt.ReceiptGlobalNo is { } global ? $" · #{global}" : string.Empty;
            return new FiscalState(Tone.Good, "Fiscalised", $"Device {receipt.FiscalDeviceId}{number}");
        }

        if (_retries.TryGetValue(e.EventId, out var retry) && retry.Succeeded)
        {
            return new FiscalState(Tone.Good, "Fiscalised", JoinDetail("on retry", retry.Message));
        }

        if (_attemptFor.TryGetValue(e.EventId, out var attempt) && attempt.IsFailure)
        {
            return new FiscalState(Tone.Bad, "Refused", JoinDetail(DeviceLabel(attempt.FiscalDeviceId), attempt.Detail ?? Humanise(attempt.Status)));
        }

        if (e.Kind == LiveTransactionKinds.Invoice)
        {
            return null;
        }

        return e.FiscalStatus switch
        {
            "Success" => new FiscalState(Tone.Good, "Fiscalised", null),
            "Failed" => new FiscalState(Tone.Bad, "Refused", e.Detail),
            "Skipped" => new FiscalState(Tone.Idle, "Not fiscalised", "skipped"),
            _ => AwaitingState(e)
        };
    }

    /// <summary>
    /// A sale with no receipt yet. Without the platform's feed no receipt can arrive here, so its age says
    /// nothing and it is not called stuck.
    /// </summary>
    private FiscalState AwaitingState(LiveTransactionEventModel sale)
    {
        if (!_fiscalAvailable)
        {
            return new FiscalState(Tone.Idle, "No receipt seen", "fiscal activity unavailable");
        }

        var age = DateTime.UtcNow - sale.OccurredAtUtc;
        return age >= WaitingAfter
            ? new FiscalState(Tone.Warn, $"Waiting {Minutes(age)}", "no receipt yet")
            : new FiscalState(Tone.Info, "Fiscalising", $"sent {Clock(sale.OccurredAtUtc)}");
    }

    private FiscalBreakdown SalesFiscalised()
    {
        int fiscalised = 0, inFlight = 0, waiting = 0, refused = 0;

        foreach (var sale in Sales)
        {
            var state = FiscalStateOf(sale);
            switch (state?.Tone)
            {
                case Tone.Good: fiscalised++; break;
                case Tone.Bad: refused++; break;
                case Tone.Warn: waiting++; break;
                case Tone.Info: inFlight++; break;
                // Skipped sales are not owed a receipt; unknown ones are not counted either way.
            }
        }

        return new FiscalBreakdown(fiscalised, inFlight, waiting, refused);
    }

    private static IEnumerable<(string Currency, decimal Total)> TotalsByCurrency(IEnumerable<LiveTransactionEventModel> events) =>
        events
            .Where(e => e.Amount is not null)
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Currency) ? "—" : e.Currency!.ToUpperInvariant())
            .Select(g => (g.Key, g.Sum(e => e.Amount!.Value)))
            .OrderByDescending(t => t.Item2);

    private int CountFor(Filter filter) => _events.Values.Count(e => Matches(e, filter));

    private DateTime? FirstEventUtc => _events.Count == 0 ? null : _events.Values.Min(e => e.OccurredAtUtc);

    // ─── Sales per hour ───────────────────────────────────────────────────

    /// <summary>
    /// Trading hours, 07:00 to 18:00, widened to take in any sale or failure outside them. A failure is
    /// counted in the hour it happened, and only while it is still open.
    /// </summary>
    private List<HourBar> SalesByHour()
    {
        var nowHour = IAuditService.ToCAT(DateTime.UtcNow).Hour;
        var sales = Sales.Select(e => IAuditService.ToCAT(e.OccurredAtUtc).Hour).ToList();
        var failures = _events.Values.Where(IsOpenFailure).Select(e => IAuditService.ToCAT(e.OccurredAtUtc).Hour).ToList();

        var first = sales.Concat(failures).DefaultIfEmpty(7).Min();
        var last = sales.Concat(failures).DefaultIfEmpty(nowHour).Max();
        var start = Math.Min(7, first);
        var end = Math.Max(Math.Max(18, nowHour), last);

        return Enumerable.Range(start, end - start + 1)
            .Select(hour => new HourBar(
                hour,
                sales.Count(h => h == hour),
                failures.Count(h => h == hour),
                Current: hour == nowHour,
                Future: hour > nowHour))
            .ToList();
    }

    // ─── Needs attention ──────────────────────────────────────────────────

    private List<Lane> AttentionLanes()
    {
        var refused = new List<AttentionCard>();
        var waiting = new List<AttentionCard>();

        foreach (var e in _events.Values.OrderByDescending(e => e.OccurredAtUtc))
        {
            if (e.Kind is LiveTransactionKinds.Sale or LiveTransactionKinds.Invoice)
            {
                var state = FiscalStateOf(e);
                if (state?.Tone == Tone.Bad)
                {
                    refused.Add(DocumentCard(e, state.Detail, RefusedState(e), Tone.Bad));
                }
                else if (state?.Tone == Tone.Warn)
                {
                    waiting.Add(DocumentCard(e, $"Sent {Clock(e.OccurredAtUtc)}. No receipt from the platform yet.", state.Word.ToLowerInvariant(), Tone.Warn));
                }
            }
            else if (e.Kind == LiveTransactionKinds.FiscalAttempt && IsOpenFailure(e))
            {
                // Fiscalised from elsewhere (the SAP bridge, the console): nothing here to retry.
                refused.Add(new AttentionCard(
                    e.EventId,
                    e.Reference ?? "Fiscal attempt",
                    e.Amount is { } amount ? Money(amount, e.Currency) : null,
                    JoinWhere(e.Location, e.Channel, DeviceLabel(e.FiscalDeviceId)),
                    e.Detail ?? Humanise(e.Status),
                    $"refused {Clock(e.OccurredAtUtc)} · fix it where it was raised",
                    Tone.Bad,
                    RetrySale: null));
            }
        }

        var days = Devices()
            .Where(d => d.Tone is Tone.Bad or Tone.Warn)
            .Select(d =>
            {
                var latest = LatestDayEvent(d.DeviceId)!;
                return new AttentionCard(
                    latest.EventId,
                    $"Device {d.DeviceId}{(d.DayNo is { } day ? $" · day {day}" : null)}",
                    null,
                    d.Location,
                    $"{latest.Detail} Fix it from the Fiscalisation platform.".TrimStart(),
                    $"{d.State.ToLowerInvariant()} · {Clock(latest.OccurredAtUtc)}",
                    d.Tone,
                    RetrySale: null);
            })
            .ToList();

        return
        [
            new Lane("Refused by the platform", Tone.Bad, "Nothing refused today.", refused),
            new Lane($"Waiting over {WaitingAfter.TotalMinutes:0} minutes", Tone.Warn,
                _fiscalAvailable ? "Nothing waiting." : "Cannot tell while fiscal activity is unavailable.", waiting),
            new Lane("Fiscal days", Tone.Bad, "No device has a day problem.", days)
        ];
    }

    private AttentionCard DocumentCard(LiveTransactionEventModel e, string? reason, string state, Tone tone) =>
        new(
            e.EventId,
            e.Reference ?? "—",
            e.Amount is { } amount ? Money(amount, e.Currency) : null,
            JoinWhere(e.Location, e.Channel, e.Counterparty),
            reason,
            state,
            tone,
            // Only a till or van sale can be retried from here; an invoice is fiscalised from SAP.
            RetrySale: e.Kind == LiveTransactionKinds.Sale && e.Reference is not null ? e : null);

    private string RefusedState(LiveTransactionEventModel document)
    {
        var tries = _events.Values.Count(x =>
            x.Kind == LiveTransactionKinds.FiscalAttempt && x.IsFailure && x.LinkedEventId == document.EventId);

        if (tries == 0)
        {
            return $"refused · sold {Clock(document.OccurredAtUtc)}";
        }

        var last = _attemptFor.TryGetValue(document.EventId, out var attempt) ? attempt.OccurredAtUtc : document.OccurredAtUtc;
        return $"{tries} {(tries == 1 ? "try" : "tries")} · last {Clock(last)}";
    }

    // ─── Devices and locations ────────────────────────────────────────────

    private LiveTransactionEventModel? LatestDayEvent(int? deviceId) =>
        deviceId is null
            ? null
            : _events.Values
                .Where(e => e.Kind == LiveTransactionKinds.FiscalDay && e.FiscalDeviceId == deviceId)
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstOrDefault();

    /// <summary>
    /// Every device the platform reported today. A device whose day was opened on an earlier day has no
    /// day event today, so its state comes from whether it is signing.
    /// </summary>
    private List<DeviceRow> Devices() =>
        _events.Values
            .Where(e => LiveTransactionKinds.IsFiscal(e.Kind) && e.FiscalDeviceId is not null)
            .GroupBy(e => e.FiscalDeviceId!.Value)
            .Select(g =>
            {
                var receipts = g.Where(e => e.Kind == LiveTransactionKinds.FiscalReceipt).ToList();
                var day = g.Where(e => e.Kind == LiveTransactionKinds.FiscalDay).MaxBy(e => e.OccurredAtUtc);
                var location = g.OrderByDescending(e => e.OccurredAtUtc)
                    .Select(e => e.Location)
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

                var (state, tone) = day is not null
                    ? (DayState(day.Status), RowTone(day))
                    : receipts.Count > 0 ? ("Signing", Tone.Good) : ("No receipts", Tone.Idle);

                return new DeviceRow(
                    g.Key,
                    location,
                    g.Max(e => e.FiscalDayNo),
                    state,
                    tone,
                    receipts.Count,
                    receipts.Count == 0 ? null : receipts.Max(e => e.OccurredAtUtc));
            })
            .OrderBy(d => d.Tone is Tone.Bad or Tone.Warn ? 0 : 1)
            .ThenBy(d => d.DeviceId)
            .ToList();

    private List<LocationRow> SalesByLocation()
    {
        var groups = Sales
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Location) ? "No warehouse" : e.Location!)
            .Select(g => (Name: g.Key, Count: g.Count(), Totals: string.Join(" · ", TotalsByCurrency(g).Take(2).Select(t => Money(t.Total, t.Currency)))))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .Take(MaxLocations)
            .ToList();

        var most = groups.Count == 0 ? 1 : groups[0].Count;
        return groups.Select(g => new LocationRow(g.Name, g.Count, g.Totals, (int)Math.Round(g.Count * 100.0 / most))).ToList();
    }

    // ─── One row's story ──────────────────────────────────────────────────

    /// <summary>
    /// What happened to a document, step by step: sold, fiscalised, posted. A sale's SAP step is what this
    /// system held when the feed read the sale, so a sale posted since still reads "not yet".
    /// </summary>
    private List<ChainStep> ChainOf(LiveTransactionEventModel e)
    {
        var steps = new List<ChainStep>();
        var fiscal = FiscalStateOf(e);

        switch (e.Kind)
        {
            case LiveTransactionKinds.Sale:
                steps.Add(new ChainStep("Sold", JoinDetail(Clock(e.OccurredAtUtc), e.Channel), Tone.Good));
                steps.Add(FiscalStep(e, fiscal));
                steps.Add(e.Status == "Posted to SAP"
                    ? new ChainStep("Posted to SAP", null, Tone.Good)
                    : new ChainStep("Not in SAP yet", $"as read at {Clock(e.OccurredAtUtc)}", Tone.Idle));
                break;

            case LiveTransactionKinds.Invoice:
                steps.Add(e.IsFailure
                    ? new ChainStep("SAP refused", Clock(e.OccurredAtUtc), Tone.Bad)
                    : new ChainStep("Raised", JoinDetail(Clock(e.OccurredAtUtc), Humanise(e.Status)), Tone.Good));
                steps.Add(FiscalStep(e, fiscal));
                break;

            case LiveTransactionKinds.IncomingPayment:
            case LiveTransactionKinds.MobilePayment:
                steps.Add(new ChainStep("Received", JoinDetail(Clock(e.OccurredAtUtc), e.Channel), e.IsFailure ? Tone.Bad : Tone.Good));
                steps.Add(new ChainStep(Humanise(e.Status), null, RowTone(e) == Tone.Idle ? Tone.Good : RowTone(e)));
                break;

            default:
                steps.Add(new ChainStep(KindLabel(e), JoinDetail(Clock(e.OccurredAtUtc), DeviceLabel(e.FiscalDeviceId)), RowTone(e)));
                steps.Add(new ChainStep(Humanise(e.Status), e.LinkedEventId is null && e.Kind != LiveTransactionKinds.FiscalDay ? "raised outside this system" : null, RowTone(e)));
                break;
        }

        return steps;
    }

    private ChainStep FiscalStep(LiveTransactionEventModel document, FiscalState? fiscal)
    {
        if (fiscal is null)
        {
            return new ChainStep("No receipt yet", null, Tone.Idle);
        }

        if (fiscal.Tone == Tone.Good && _receiptFor.TryGetValue(document.EventId, out var receipt))
        {
            return new ChainStep("Fiscalised", JoinDetail(Clock(receipt.OccurredAtUtc), fiscal.Detail), Tone.Good);
        }

        return fiscal.Tone == Tone.Bad
            ? new ChainStep("Refused", RefusedState(document), Tone.Bad)
            : new ChainStep(fiscal.Word, fiscal.Detail, fiscal.Tone);
    }

    /// <summary>The reason under an opened row: a refusal or a failure in words.</summary>
    private string? NoteOf(LiveTransactionEventModel e)
    {
        if (_retries.TryGetValue(e.EventId, out var retry) && !retry.Busy && !retry.Succeeded)
        {
            return retry.Message;
        }

        var fiscal = FiscalStateOf(e);
        if (fiscal?.Tone == Tone.Bad)
        {
            return fiscal.Detail;
        }

        return e.IsFailure || e.Kind == LiveTransactionKinds.FiscalDay ? e.Detail : null;
    }

    /// <summary>The status column: a document's fiscal state, otherwise the event's own status.</summary>
    private (Tone Tone, string Word, string? Detail) StatusOf(LiveTransactionEventModel e)
    {
        if (FiscalStateOf(e) is { } fiscal)
        {
            return (fiscal.Tone, fiscal.Word, fiscal.Detail);
        }

        if (e.Kind == LiveTransactionKinds.Invoice)
        {
            return (e.IsFailure ? Tone.Bad : Tone.Idle, e.IsFailure ? "SAP refused" : "No receipt yet", null);
        }

        var detail = LiveTransactionKinds.IsFiscal(e.Kind)
            ? JoinDetail(DeviceLabel(e.FiscalDeviceId), e.ReceiptGlobalNo is { } g ? $"#{g}" : null)
            : e.Channel;
        return (RowTone(e), Humanise(e.Status), detail);
    }

    // ─── Formatting ───────────────────────────────────────────────────────

    private static DateTime TodayCat() => IAuditService.ToCAT(DateTime.UtcNow).Date;

    private static string Clock(DateTime utc) =>
        IAuditService.ToCAT(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string ShortClock(DateTime utc) =>
        IAuditService.ToCAT(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes < 60
            ? $"{(int)span.TotalMinutes} min"
            : $"{(int)span.TotalHours} h {span.Minutes} min";

    private static string Money(decimal amount, string? currency) =>
        $"{(string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.ToUpperInvariant() + " ")}{amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string Percent(int part, int whole) =>
        whole == 0 ? "0" : (part * 100.0 / whole).ToString("0.###", CultureInfo.InvariantCulture);

    private static string? DeviceLabel(int? deviceId) => deviceId is { } id ? $"Device {id}" : null;

    private static string KindLabel(LiveTransactionEventModel e) => e.Kind switch
    {
        LiveTransactionKinds.Sale => "Sale",
        LiveTransactionKinds.Invoice => "Invoice",
        LiveTransactionKinds.IncomingPayment => "Payment",
        LiveTransactionKinds.MobilePayment => "Mobile pay",
        LiveTransactionKinds.FiscalReceipt => e.ReceiptType switch
        {
            "CreditNote" => "Fiscal CN",
            "DebitNote" => "Fiscal DN",
            _ => "Receipt"
        },
        LiveTransactionKinds.FiscalAttempt => "Fiscal try",
        LiveTransactionKinds.FiscalDay => "Fiscal day",
        _ => e.Kind
    };

    private static string KindClass(LiveTransactionEventModel e) => e.Kind switch
    {
        LiveTransactionKinds.Sale => "ltx-kind-sale",
        LiveTransactionKinds.Invoice => "ltx-kind-invoice",
        LiveTransactionKinds.IncomingPayment or LiveTransactionKinds.MobilePayment => "ltx-kind-pay",
        _ => "ltx-kind-fiscal"
    };

    private static Tone RowTone(LiveTransactionEventModel e)
    {
        if (e.IsFailure)
        {
            return Tone.Bad;
        }

        return e.Kind switch
        {
            LiveTransactionKinds.FiscalReceipt => e.Status == "CapturedOffline" ? Tone.Warn : Tone.Good,
            LiveTransactionKinds.FiscalAttempt => e.Status is "RetryPending" or "Retrying" ? Tone.Warn : Tone.Good,
            LiveTransactionKinds.FiscalDay => e.Status.EndsWith("Succeeded", StringComparison.Ordinal) ? Tone.Good : Tone.Warn,
            LiveTransactionKinds.MobilePayment => e.Status is "Pending" or "Initiated" ? Tone.Warn : Tone.Good,
            _ => Tone.Idle
        };
    }

    private static string ToneClass(Tone tone) => tone switch
    {
        Tone.Good => "ltx-tone-good",
        Tone.Info => "ltx-tone-info",
        Tone.Warn => "ltx-tone-warn",
        Tone.Bad => "ltx-tone-bad",
        _ => "ltx-tone-idle"
    };

    private static string StepIcon(Tone tone) => tone switch
    {
        Tone.Good => "ph-check",
        Tone.Bad => "ph-x",
        Tone.Warn => "ph-clock",
        Tone.Info => "ph-arrows-clockwise",
        _ => "ph-minus"
    };

    /// <summary>
    /// A fiscal day's state for the device list. The feed sends "{action} {status}"; the two good
    /// outcomes read better as what the day now is.
    /// </summary>
    private static string DayState(string status) => status switch
    {
        "Open Succeeded" => "Day open",
        "Close Succeeded" => "Day closed",
        _ => Humanise(status)
    };

    /// <summary>
    /// Splits the platform's PascalCase names into words, and lowers a capital that starts a later
    /// word: RetryPending -> Retry pending, "Close Failed" -> Close failed.
    /// </summary>
    private static string Humanise(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var chars = new List<char>(value.Length + 8) { value[0] };
        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]) && value[i - 1] != ' ')
            {
                chars.Add(' ');
                chars.Add(char.ToLowerInvariant(value[i]));
            }
            else if (char.IsUpper(value[i]) && value[i - 1] == ' ' && (i + 1 >= value.Length || !char.IsUpper(value[i + 1])))
            {
                chars.Add(char.ToLowerInvariant(value[i]));
            }
            else
            {
                chars.Add(value[i]);
            }
        }

        return new string(chars.ToArray());
    }

    private static string? JoinDetail(string? first, string? second) =>
        string.IsNullOrWhiteSpace(second) ? first
        : string.IsNullOrWhiteSpace(first) ? second
        : $"{first} · {second}";

    private static string? JoinWhere(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return kept.Count == 0 ? null : string.Join(" · ", kept);
    }

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
