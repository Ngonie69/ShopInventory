namespace ShopInventory.Features.Maintenance;

/// <summary>
/// The outcome of <see cref="MaintenanceGate.Evaluate"/>: let it through, or refuse it and
/// say so in these terms.
/// </summary>
public sealed record MaintenanceDecision
{
    public required bool IsBlocked { get; init; }

    /// <summary>What to tell the app. Only set when blocked.</summary>
    public string? Message { get; init; }

    /// <summary>When the lockout lifts by itself, if an operator set an end.</summary>
    public DateTime? EndsAtUtc { get; init; }

    /// <summary>What to put on <c>Retry-After</c>. Only meaningful when blocked.</summary>
    public TimeSpan RetryAfter { get; init; }

    /// <summary>The scope in force, so the app can tell a full stop from a read-only window.</summary>
    public MaintenanceScope Scope { get; init; }

    public static readonly MaintenanceDecision Allowed = new() { IsBlocked = false };

    public static MaintenanceDecision Blocked(MaintenanceState state, TimeSpan retryAfter) => new()
    {
        IsBlocked = true,
        Message = state.ResolveMessage(),
        EndsAtUtc = state.EndsAtUtc,
        RetryAfter = retryAfter,
        Scope = state.Scope
    };
}
