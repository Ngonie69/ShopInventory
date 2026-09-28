using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Whether the cluster currently considers SAP down, as the availability probe last recorded it.
/// </summary>
/// <remarks>
/// <para>
/// The per-node circuit breaker trips on five failures and resets thirty seconds later, and each node
/// trips its own. That is the right shape for protecting one node from hammering SAP, and the wrong
/// one for deciding what background work should do: during an outage the breaker is closed half the
/// time, so every pass that starts in a closed window spends a round trip finding out again.
/// </para>
/// <para>
/// This is the other answer. <see cref="SapAvailabilityProbe"/> probes on one node and records an
/// outage in <c>SapOutages</c>; every node keeps the open row in memory, re-read every
/// <see cref="RefreshInterval"/> by <see cref="SapAvailabilityRefresher"/>. The node that writes a
/// change applies it at once. Reading it never touches the database.
/// </para>
/// <para>
/// Background passes consult it through <see cref="SapCircuitBreakerState.ShouldHoldBackWork"/>. It
/// does not stop a request from being sent: a person pressing Post, or the probe itself, still reaches
/// SAP, which is how a wrongly declared outage costs a minute rather than a day.
/// </para>
/// </remarks>
public sealed class SapAvailability(
    IServiceScopeFactory scopeFactory,
    ILogger<SapAvailability> logger)
{
    /// <summary>How often each node re-reads the outage state another node may have written.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private volatile SapAvailabilityState _current = SapAvailabilityState.Available;

    /// <summary>The state this node last read or wrote. Never touches the database.</summary>
    public SapAvailabilityState Current => _current;

    /// <summary>Whether an outage is open, as this node last knew it.</summary>
    public bool IsInOutage => _current.InOutage;

    /// <summary>Reads the open outage, if any, and updates this node's copy.</summary>
    /// <remarks>If the read fails, the last known state stays in force.</remarks>
    public async Task<SapAvailabilityState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var open = await context.SapOutages
                .AsNoTracking()
                .Where(outage => outage.EndedAtUtc == null)
                .OrderBy(outage => outage.Id)
                .Select(outage => new { outage.Id, outage.StartedAtUtc, outage.Cause })
                .FirstOrDefaultAsync(cancellationToken);

            var state = open is null
                ? SapAvailabilityState.Available
                : new SapAvailabilityState(open.Id, open.StartedAtUtc, open.Cause);

            Apply(state);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the SAP outage state; keeping {State}.", Describe(_current));
            return _current;
        }
    }

    /// <summary>Takes a state the caller has just written, without waiting for the next refresh.</summary>
    public void Apply(SapAvailabilityState state)
    {
        var previous = _current;
        _current = state;

        if (previous.OutageId != state.OutageId)
        {
            logger.LogWarning("SAP availability is now {State} (was {Previous}).", Describe(state), Describe(previous));
        }
    }

    public static SapAvailabilityState StateOf(SapOutageEntity? open) =>
        open is null || open.EndedAtUtc is not null
            ? SapAvailabilityState.Available
            : new SapAvailabilityState(open.Id, open.StartedAtUtc, open.Cause);

    private static string Describe(SapAvailabilityState state) =>
        state.InOutage ? $"down since {state.SinceUtc:u} (outage {state.OutageId}, {state.Cause})" : "available";
}

/// <param name="OutageId">The open <c>SapOutages</c> row, or null when SAP is available.</param>
/// <param name="SinceUtc">When the open outage started.</param>
/// <param name="Cause">A <see cref="SapOutageCauses"/> value.</param>
public sealed record SapAvailabilityState(int? OutageId, DateTime? SinceUtc, string? Cause)
{
    public static readonly SapAvailabilityState Available = new(null, null, null);

    public bool InOutage => OutageId is not null;
}

/// <summary>Keeps each node's copy of <see cref="SapAvailability"/> current.</summary>
/// <remarks>
/// Registered before the Quartz scheduler, like the connection switch's refresher, so a node that
/// starts during an outage knows it before its first posting pass.
/// </remarks>
public sealed class SapAvailabilityRefresher(SapAvailability availability) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await availability.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SapAvailability.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await availability.RefreshAsync(stoppingToken);
        }
    }
}
