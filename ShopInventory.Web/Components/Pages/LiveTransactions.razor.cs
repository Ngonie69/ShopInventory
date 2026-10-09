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
/// The page holds the whole day's events and derives every count from them, so the tiles and the list
/// can never disagree. It reads the day once on open, then polls every <see cref="PollInterval"/> from a
/// little before the newest event it holds, because a row stamped earlier can commit later; re-reading
/// that overlap is harmless since events are keyed by id. A sale announced over the hub triggers a poll
/// at once rather than waiting for the next tick.
/// </remarks>
public partial class LiveTransactions : IAsyncDisposable
{
    private const int PageLimit = 500;
    private const int MaxPagesPerRefresh = 40;
    private const int MaxHeldEvents = 20_000;
    private const int MaxRows = 250;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private INotificationHubService HubService { get; set; } = default!;

    private readonly Dictionary<string, LiveTransactionEventModel> _events = new(StringComparer.Ordinal);

    // The newest fiscal receipt and attempt for each sale or invoice, by that document's event id.
    private readonly Dictionary<string, LiveTransactionEventModel> _receiptFor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveTransactionEventModel> _attemptFor = new(StringComparer.Ordinal);

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
    private DateTime? _lastUpdatedUtc;
    private bool _fiscalAvailable = true;
    private string? _fiscalMessage;
    private bool _trimmed;

    private Filter _filter = Filter.All;
    private string _search = string.Empty;

    private PeriodicTimer? _pollTimer;
    private Task? _pollTask;

    private enum Filter { All, Sales, Money, Fiscal, Failures }

    private enum Tone { Good, Warn, Bad, Idle }

    private sealed record FiscalState(Tone Tone, string Word, string? Detail);

    private sealed record DeviceDay(int DeviceId, LiveTransactionEventModel Latest);

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
                await InvokeAsync(() => RefreshAsync(initial: false));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSaleCreated()
    {
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
                // Midnight CAT: the tiles are "today", so the day starts empty again.
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
        _freshUntilUtc.Clear();
        _cursorUtc = null;
        _caughtUp = false;
        _trimmed = false;
    }

    /// <summary>
    /// A very busy day is capped rather than left to grow the circuit without bound. The tiles then
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

    // ─── What the page shows ──────────────────────────────────────────────

    private void SetFilter(Filter filter) => _filter = filter;

    private bool IsFresh(string eventId) =>
        _freshUntilUtc.TryGetValue(eventId, out var until) && until > DateTime.UtcNow;

    private IEnumerable<LiveTransactionEventModel> Sales =>
        _events.Values.Where(e => e.Kind == LiveTransactionKinds.Sale);

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
        // A fiscal receipt or attempt for a document already on the list is drawn under that document,
        // not again as a row of its own.
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
        if (e.Kind == LiveTransactionKinds.Sale)
        {
            return FiscalStateOf(e)?.Tone == Tone.Bad;
        }

        if (!e.IsFailure)
        {
            return false;
        }

        if (e.Kind == LiveTransactionKinds.FiscalAttempt && e.LinkedEventId is { } target)
        {
            // Counted once, on the document, when the document is held.
            return !_events.ContainsKey(target) && !_receiptFor.ContainsKey(target);
        }

        return true;
    }

    private int OpenFailureCount => _events.Values.Count(IsOpenFailure);

    /// <summary>
    /// The fiscal state of a sale or invoice: a receipt the platform reported beats anything this system
    /// last wrote down, because this system only learns of it on its next status sync.
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
            return new FiscalState(Tone.Good, "Fiscalised", $"device {receipt.FiscalDeviceId}{number}");
        }

        if (_attemptFor.TryGetValue(e.EventId, out var attempt) && attempt.IsFailure)
        {
            return new FiscalState(Tone.Bad, "Fiscal failed", JoinDetail(Humanise(attempt.Status), attempt.Detail));
        }

        if (e.Kind == LiveTransactionKinds.Invoice)
        {
            return null;
        }

        return e.FiscalStatus switch
        {
            "Success" => new FiscalState(Tone.Good, "Fiscalised", null),
            "Failed" => new FiscalState(Tone.Bad, "Fiscal failed", e.Detail),
            "Skipped" => new FiscalState(Tone.Idle, "Not fiscalised", "skipped"),
            _ => new FiscalState(Tone.Warn, "Awaiting fiscalisation", null)
        };
    }

    private (int Fiscalised, int Total) SalesFiscalised()
    {
        var total = 0;
        var fiscalised = 0;

        foreach (var sale in Sales)
        {
            var state = FiscalStateOf(sale);
            if (state?.Word == "Not fiscalised")
            {
                continue;
            }

            total++;
            if (state?.Tone == Tone.Good)
            {
                fiscalised++;
            }
        }

        return (fiscalised, total);
    }

    private IEnumerable<(string Currency, decimal Total)> TotalsByCurrency(IEnumerable<LiveTransactionEventModel> events) =>
        events
            .Where(e => e.Amount is not null)
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Currency) ? "—" : e.Currency!.ToUpperInvariant())
            .Select(g => (g.Key, g.Sum(e => e.Amount!.Value)))
            .OrderByDescending(t => t.Item2);

    private IEnumerable<LiveTransactionEventModel> Payments =>
        _events.Values.Where(e =>
            e.Kind is LiveTransactionKinds.IncomingPayment or LiveTransactionKinds.MobilePayment && !e.IsFailure);

    private List<DeviceDay> FiscalDays() =>
        _events.Values
            .Where(e => e.Kind == LiveTransactionKinds.FiscalDay && e.FiscalDeviceId is not null)
            .GroupBy(e => e.FiscalDeviceId!.Value)
            .Select(g => new DeviceDay(g.Key, g.OrderByDescending(e => e.OccurredAtUtc).First()))
            .OrderBy(d => d.DeviceId)
            .ToList();

    private int FiscalReceiptCount => _events.Values.Count(e => e.Kind == LiveTransactionKinds.FiscalReceipt);

    private int CountFor(Filter filter) => _events.Values.Count(e => Matches(e, filter));

    // ─── Formatting ───────────────────────────────────────────────────────

    private static DateTime TodayCat() => IAuditService.ToCAT(DateTime.UtcNow).Date;

    private static string Clock(DateTime utc) =>
        IAuditService.ToCAT(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Money(decimal amount, string? currency) =>
        $"{(string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.ToUpperInvariant() + " ")}{amount.ToString("N2", CultureInfo.InvariantCulture)}";

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
            _ => "Fiscal"
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
        Tone.Warn => "ltx-tone-warn",
        Tone.Bad => "ltx-tone-bad",
        _ => "ltx-tone-idle"
    };

    /// <summary>Splits the platform's PascalCase names into words: RetryPending -> Retry pending.</summary>
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
        : $"{first} — {second}";

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
