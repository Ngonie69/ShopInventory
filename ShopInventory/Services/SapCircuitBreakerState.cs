using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

public sealed class SapCircuitBreakerState(IOptions<SAPSettings> settings)
{
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTime? _lastFailureUtc;
    private DateTime? _openUntilUtc;
    private string? _lastFailure;

    public bool IsOpen
    {
        get
        {
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
        bool IsOpen,
        int ConsecutiveFailures,
        string? LastFailure,
        DateTime? LastFailureUtc,
        DateTime? OpenUntilUtc,
        int FailureThreshold,
        TimeSpan BreakDuration);
}