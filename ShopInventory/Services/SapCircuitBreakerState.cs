using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

/// <remarks>
/// Also answers for <see cref="SapConnectionSwitch"/>. When an admin turns SAP off, the circuit reads
/// as open, so every caller that already holds work back while the circuit is open (the posting jobs,
/// and the desktop invoice, transfer and payment endpoints that queue instead of posting) does the same
/// while SAP is off. The connection switch is optional so that code built without it, such as a test,
/// keeps the breaker's own behaviour.
/// </remarks>
public sealed class SapCircuitBreakerState(
    IOptions<SAPSettings> settings,
    SapConnectionSwitch? connectionSwitch = null)
{
    /// <summary>
    /// The wait reported to callers while SAP is switched off. It is not a promise that SAP comes back
    /// on then. It only has to be longer than the client's transient retries, so they give up at once
    /// instead of sleeping on a refusal that cannot clear. It is finite because callers do arithmetic on it.
    /// </summary>
    public static readonly TimeSpan SwitchedOffRetryAfter = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTime? _lastFailureUtc;
    private DateTime? _openUntilUtc;
    private string? _lastFailure;

    /// <summary>Whether an admin has turned the SAP connection off in Settings.</summary>
    public bool IsSwitchedOff => connectionSwitch is { IsEnabled: false };

    public bool IsOpen
    {
        get
        {
            if (IsSwitchedOff)
            {
                return true;
            }

            lock (_gate)
            {
                return _openUntilUtc.HasValue && _openUntilUtc.Value > DateTime.UtcNow;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntilUtc = null;
        }
    }

    public void RecordFailure(string? reason)
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            _lastFailureUtc = DateTime.UtcNow;
            _lastFailure = string.IsNullOrWhiteSpace(reason) ? null : reason;

            if (_consecutiveFailures >= GetFailureThreshold())
            {
                _openUntilUtc = DateTime.UtcNow.Add(GetBreakDuration());
            }
        }
    }

    public bool ShouldShortCircuit(out TimeSpan retryAfter)
    {
        if (IsSwitchedOff)
        {
            retryAfter = SwitchedOffRetryAfter;
            return true;
        }

        var snapshot = GetSnapshot();
        if (snapshot.IsOpen && snapshot.OpenUntilUtc.HasValue)
        {
            retryAfter = snapshot.OpenUntilUtc.Value - DateTime.UtcNow;
            return true;
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    public SapCircuitSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var failureThreshold = GetFailureThreshold();
            var breakDuration = GetBreakDuration();
            var isOpen = _openUntilUtc.HasValue && _openUntilUtc.Value > DateTime.UtcNow;

            return new SapCircuitSnapshot(
                settings.Value.Enabled,
                IsSwitchedOff,
                isOpen,
                _consecutiveFailures,
                _lastFailure,
                _lastFailureUtc,
                _openUntilUtc,
                failureThreshold,
                breakDuration);
        }
    }

    private int GetFailureThreshold() => Math.Max(1, settings.Value.CircuitFailureThreshold);

    private TimeSpan GetBreakDuration() => TimeSpan.FromSeconds(Math.Max(5, settings.Value.CircuitBreakDurationSeconds));

    public sealed record SapCircuitSnapshot(
        bool IsEnabled,
        bool IsSwitchedOff,
        bool IsOpen,
        int ConsecutiveFailures,
        string? LastFailure,
        DateTime? LastFailureUtc,
        DateTime? OpenUntilUtc,
        int FailureThreshold,
        TimeSpan BreakDuration);
}