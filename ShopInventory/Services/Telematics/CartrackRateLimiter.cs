using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.Telematics;

/// <summary>The named request budgets Cartrack publish separate ceilings for.</summary>
public static class CartrackBudget
{
    /// <summary>Everything without its own published limit. 1,000 a minute.</summary>
    public const string Global = "global";

    /// <summary><c>GET /vehicles/status</c>. 60 a minute.</summary>
    public const string Status = "status";

    /// <summary><c>GET /vehicles/events</c>. 60 a minute, and each page is a request.</summary>
    public const string Events = "events";
}

/// <summary>
/// Holds this process inside Cartrack's published request ceilings.
/// </summary>
/// <remarks>
/// <para>
/// Waiting before a request is cheaper than being throttled after one: Cartrack warn that
/// repeated violations can restrict access, and the account is shared with whatever else the
/// business runs against it. The 429 handling in <see cref="CartrackClient"/> is the net; this is
/// what stops the fall.
/// </para>
/// <para>
/// <b>The budget is per process, not per account.</b> That is sufficient today only because every
/// caller is a Quartz job, and Quartz's clustered store runs one instance of a job key at a time
/// across the whole cluster. Anything that calls the client from a request path — a page, a
/// controller — breaks that assumption silently, because the limiter will still say yes.
/// </para>
/// </remarks>
public interface ICartrackRateLimiter
{
    /// <summary>Waits until one request may be made against the named budget.</summary>
    Task WaitAsync(string budget, CancellationToken cancellationToken);

    /// <summary>
    /// Holds the named budget shut for at least this long, after a 429 told us to. Applied to the
    /// global budget as well, because a throttle is about the account and not only the endpoint.
    /// </summary>
    void Pause(string budget, TimeSpan duration);
}

/// <inheritdoc cref="ICartrackRateLimiter"/>
public sealed class CartrackRateLimiter(IOptions<CartrackSettings> settings) : ICartrackRateLimiter
{
    private readonly CartrackSettings _settings = settings.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Budget> _budgets = new(StringComparer.Ordinal);

    private sealed class Budget
    {
        public required int PerMinute { get; init; }
        public Queue<DateTime> Recent { get; } = new();
        public DateTime PausedUntilUtc { get; set; }
    }

    public async Task WaitAsync(string budget, CancellationToken cancellationToken)
    {
        // Every request counts against the global ceiling as well as its own, so both are taken.
        // Global first: a caller that blocks on the narrower budget while holding room in the
        // wider one has reserved nothing anybody else can use.
        if (!string.Equals(budget, CartrackBudget.Global, StringComparison.Ordinal))
        {
            await TakeAsync(CartrackBudget.Global, cancellationToken);
        }

        await TakeAsync(budget, cancellationToken);
    }

    public void Pause(string budget, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var until = DateTime.UtcNow.Add(duration);

        _gate.Wait();

        try
        {
            Hold(budget).PausedUntilUtc = Later(Hold(budget).PausedUntilUtc, until);

            if (!string.Equals(budget, CartrackBudget.Global, StringComparison.Ordinal))
            {
                Hold(CartrackBudget.Global).PausedUntilUtc =
                    Later(Hold(CartrackBudget.Global).PausedUntilUtc, until);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TakeAsync(string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;

            await _gate.WaitAsync(cancellationToken);

            try
            {
                var budget = Hold(name);
                var now = DateTime.UtcNow;

                while (budget.Recent.Count > 0 && now - budget.Recent.Peek() >= TimeSpan.FromMinutes(1))
                {
                    budget.Recent.Dequeue();
                }

                var pausedFor = budget.PausedUntilUtc > now ? budget.PausedUntilUtc - now : TimeSpan.Zero;

                // Under the ceiling and not paused: take the slot and go.
                if (pausedFor == TimeSpan.Zero && budget.Recent.Count < budget.PerMinute)
                {
                    budget.Recent.Enqueue(now);
                    return;
                }

                // Otherwise wait for whichever frees first — the pause lifting, or the oldest
                // request in the window ageing out of it.
                var slotFreesIn = budget.Recent.Count > 0
                    ? TimeSpan.FromMinutes(1) - (now - budget.Recent.Peek())
                    : TimeSpan.Zero;

                wait = Later(pausedFor, slotFreesIn);

                if (wait < TimeSpan.FromMilliseconds(50))
                {
                    wait = TimeSpan.FromMilliseconds(50);
                }
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(wait, cancellationToken);
        }
    }

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private Budget Hold(string name)
    {
        if (_budgets.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var created = new Budget { PerMinute = Math.Max(1, CeilingFor(name)) };
        _budgets[name] = created;

        return created;
    }

    private int CeilingFor(string name) => name switch
    {
        CartrackBudget.Status => _settings.StatusRequestsPerMinute,
        CartrackBudget.Events => _settings.EventsRequestsPerMinute,
        _ => _settings.GlobalRequestsPerMinute
    };

    private static TimeSpan Later(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static DateTime Later(DateTime left, DateTime right) => left > right ? left : right;
}
