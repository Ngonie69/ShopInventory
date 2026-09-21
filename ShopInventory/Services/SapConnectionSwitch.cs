using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// The admin's on/off switch for all traffic to the SAP Service Layer.
/// </summary>
/// <remarks>
/// <para>
/// Off, every SAP request is refused before it leaves the process, the same way an open circuit
/// refuses it (see <see cref="SapCircuitBreakerState"/>). The app already treats that refusal as
/// "SAP is unavailable, nothing was sent". Invoices, transfers and payments stay queued, the posting
/// jobs skip their pass, and the queued documents post by themselves once the switch is back on.
/// This is for SAP maintenance, a company-database move, or an incident where the app must stop
/// writing to SAP, and none of those should need a deploy or a restart.
/// </para>
/// <para>
/// This is not <c>SAP:Enabled</c>. That configuration flag only decides whether a few jobs are
/// scheduled at all. It never stopped the invoice, transfer or payment posting jobs, so it cannot
/// stop the app from writing to SAP.
/// </para>
/// <para>
/// The switch is stored in <c>SystemConfigs</c> so that every API node obeys it. Each node keeps the
/// value in memory, because the circuit breaker asks for it on every SAP request, and
/// <see cref="SapConnectionSwitchRefresher"/> re-reads it every <see cref="RefreshInterval"/>. The
/// node that saves the switch applies it at once. Other nodes follow within one interval. With no
/// row saved, the connection is on.
/// </para>
/// </remarks>
public sealed class SapConnectionSwitch(
    IServiceScopeFactory scopeFactory,
    ILogger<SapConnectionSwitch> logger)
{
    /// <summary>The <c>SystemConfigs</c> key holding the switch.</summary>
    public const string ConfigKey = "SAP.ConnectionEnabled";

    /// <summary>How often each node re-reads the switch that another node may have saved.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private volatile SapConnectionSwitchState _current = new(true, null);

    /// <summary>Whether SAP requests may be sent, as this node last read it. Never touches the database.</summary>
    public bool IsEnabled => _current.Enabled;

    /// <summary>The state this node last read, without touching the database.</summary>
    public SapConnectionSwitchState Current => _current;

    /// <summary>Reads the switch from the database and updates this node's copy.</summary>
    /// <remarks>If the read fails, the last known value stays in force.</remarks>
    public async Task<SapConnectionSwitchState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var row = await context.SystemConfigs
                .AsNoTracking()
                .Where(config => config.Key == ConfigKey)
                .Select(config => new { config.Value, config.UpdatedAt })
                .FirstOrDefaultAsync(cancellationToken);

            var state = row is not null && bool.TryParse(row.Value, out var enabled)
                ? new SapConnectionSwitchState(enabled, row.UpdatedAt)
                : new SapConnectionSwitchState(true, null);

            Apply(state);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the SAP connection switch; keeping it {State}.", Describe(_current.Enabled));
            return _current;
        }
    }

    public async Task<SapConnectionSwitchState> SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        var row = await context.SystemConfigs.FirstOrDefaultAsync(config => config.Key == ConfigKey, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = ConfigKey,
                ValueType = "bool",
                Category = "SAP",
                Description =
                    "Whether the API may send requests to the SAP Service Layer. Off: every SAP request is "
                    + "refused, and invoices, transfers and payments stay queued until it is turned back on.",
                IsEditable = true
            };
            context.SystemConfigs.Add(row);
        }

        row.Value = enabled ? "true" : "false";
        row.UpdatedAt = now;

        await context.SaveChangesAsync(cancellationToken);

        var state = new SapConnectionSwitchState(enabled, now);
        Apply(state);
        return state;
    }

    private void Apply(SapConnectionSwitchState state)
    {
        var previous = _current;
        _current = state;

        if (previous.Enabled != state.Enabled)
        {
            logger.LogWarning("SAP connection switched {State}.", Describe(state.Enabled));
        }
    }

    private static string Describe(bool enabled) => enabled ? "ON" : "OFF";
}

/// <param name="Enabled">Whether SAP requests may be sent.</param>
/// <param name="UpdatedAtUtc">When it was last saved from Settings. Null while the default (on) applies.</param>
public sealed record SapConnectionSwitchState(bool Enabled, DateTime? UpdatedAtUtc);

/// <summary>
/// Keeps each node's copy of <see cref="SapConnectionSwitch"/> current.
/// </summary>
/// <remarks>
/// The first read runs in <see cref="StartAsync"/>. It is registered before the Quartz scheduler, so a
/// node that starts while the switch is off does not post anything before it has read the switch.
/// </remarks>
public sealed class SapConnectionSwitchRefresher(SapConnectionSwitch connectionSwitch) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await connectionSwitch.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SapConnectionSwitch.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await connectionSwitch.RefreshAsync(stoppingToken);
        }
    }
}
