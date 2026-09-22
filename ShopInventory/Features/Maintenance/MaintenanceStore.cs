using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.AppVersion;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Maintenance;

/// <inheritdoc />
public sealed class MaintenanceStore : IMaintenanceStore
{
    /// <summary>How long a snapshot is served before a read triggers a refresh.</summary>
    /// <remarks>
    /// Shorter than the rate limiter's, deliberately. Both are "how long until every node agrees",
    /// but the consequence differs: a stale rate limit lets a few extra requests through, while a
    /// stale maintenance switch lets an invoice post into a database that is being restored. Five
    /// seconds is the window an operator waits after throwing the switch before the last node stops
    /// accepting work.
    /// </remarks>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    public const string EnabledKey = "Mobile.Maintenance.Enabled";
    public const string ScopeKey = "Mobile.Maintenance.Scope";
    public const string MessageKey = "Mobile.Maintenance.Message";
    public const string AppsKey = "Mobile.Maintenance.Apps";
    public const string StartedAtKey = "Mobile.Maintenance.StartedAtUtc";
    public const string EndsAtKey = "Mobile.Maintenance.EndsAtUtc";
    public const string UpdatedByKey = "Mobile.Maintenance.UpdatedBy";

    private const string ConfigCategory = "Maintenance";

    private static readonly string[] AllKeys =
    [
        EnabledKey, ScopeKey, MessageKey, AppsKey, StartedAtKey, EndsAtKey, UpdatedByKey
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MaintenanceStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile Snapshot _snapshot;

    private sealed record Snapshot(MaintenanceState State, DateTimeOffset LoadedAt);

    public MaintenanceStore(
        IServiceScopeFactory scopeFactory,
        ILogger<MaintenanceStore> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Off until the database says otherwise. Starting from "locked out" would mean a database
        // that cannot be reached takes the phones down with it, which is the opposite of what an
        // operator wants from a switch they did not throw.
        _snapshot = new Snapshot(MaintenanceState.Off, DateTimeOffset.MinValue);
    }

    /// <inheritdoc />
    public MaintenanceState Current
    {
        get
        {
            var snapshot = _snapshot;
            if (_timeProvider.GetUtcNow() - snapshot.LoadedAt >= RefreshInterval)
            {
                BeginRefresh();
            }

            return snapshot.State;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(MaintenanceState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await context.SystemConfigs
            .Where(config => AllKeys.Contains(config.Key))
            .ToListAsync(cancellationToken);

        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value, type) in Describe(state))
        {
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Value = value;
                existing.ValueType = type;
                existing.UpdatedAt = now;
            }
            else
            {
                context.SystemConfigs.Add(NewRow(key, value, type, now));
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        // Applied here at once rather than waiting out the refresh interval, so the operator who
        // threw the switch sees it hold on the node they are talking to.
        _snapshot = new Snapshot(state, _timeProvider.GetUtcNow());

        _logger.LogWarning(
            "Mobile maintenance lockout set to {Enabled} ({Scope}) by {User}. Apps={Apps}, EndsAtUtc={EndsAtUtc}",
            state.Enabled,
            state.Scope,
            state.UpdatedBy ?? "unknown",
            state.AppIds.Count == 0 ? "all" : string.Join(",", state.AppIds),
            state.EndsAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none");
    }

    /// <inheritdoc />
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(cancellationToken);
        _snapshot = new Snapshot(loaded, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Refresh off the request path. A failure keeps the previous snapshot: a lockout a few seconds
    /// out of date beats hammering a database that is already being worked on, and beats a throw on
    /// the request path.
    /// </summary>
    private void BeginRefresh()
    {
        if (!_refreshGate.Wait(0))
        {
            return;   // another request is already reloading
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await LoadAsync(CancellationToken.None);
                _snapshot = new Snapshot(loaded, _timeProvider.GetUtcNow());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reload the mobile maintenance switch; keeping the current state.");

                // Stamp the attempt so a database that is down is retried on the interval rather
                // than on every single request.
                _snapshot = new Snapshot(_snapshot.State, _timeProvider.GetUtcNow());
            }
            finally
            {
                _refreshGate.Release();
            }
        });
    }

    private async Task<MaintenanceState> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await context.SystemConfigs
            .AsNoTracking()
            .Where(config => AllKeys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(cancellationToken);

        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);

        return new MaintenanceState(
            Enabled: ReadBool(values, EnabledKey),
            Scope: ReadScope(values),
            Message: ReadString(values, MessageKey),
            AppIds: ReadAppIds(values),
            StartedAtUtc: ReadDate(values, StartedAtKey),
            EndsAtUtc: ReadDate(values, EndsAtKey),
            UpdatedBy: ReadString(values, UpdatedByKey));
    }

    private static IEnumerable<(string Key, string Value, string Type)> Describe(MaintenanceState state) =>
    [
        (EnabledKey, state.Enabled ? "true" : "false", "bool"),
        (ScopeKey, state.Scope.ToString(), "string"),
        (MessageKey, state.Message?.Trim() ?? string.Empty, "string"),
        (AppsKey, JsonSerializer.Serialize(state.AppIds), "json"),
        (StartedAtKey, FormatDate(state.StartedAtUtc), "string"),
        (EndsAtKey, FormatDate(state.EndsAtUtc), "string"),
        (UpdatedByKey, state.UpdatedBy?.Trim() ?? string.Empty, "string")
    ];

    private static string FormatDate(DateTime? value) =>
        value is null ? string.Empty : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static SystemConfigEntity NewRow(string key, string value, string type, DateTime now) => new()
    {
        Key = key,
        Value = value,
        ValueType = type,
        Category = ConfigCategory,
        Description = Descriptions.TryGetValue(key, out var text) ? text : null,
        IsEditable = true,
        UpdatedAt = now
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [EnabledKey] =
            "Whether the mobile apps are locked out for maintenance. On, the API refuses what the scope says "
            + "and answers 503 with the message below.",
        [ScopeKey] =
            "Transactions (the default) refuses anything that changes something and leaves reads working. "
            + "All refuses reads too, leaving only signing in and the status check.",
        [MessageKey] = "What the apps show while the lockout is on. Blank uses the built-in wording.",
        [AppsKey] =
            "JSON array of app keys the lockout covers. An empty array covers every app, which is the usual case. "
            + "Keys: " + "cheeseman-driver, kefalos-so, kefalos-vansales, kefalos-customer-orders.",
        [StartedAtKey] = "When the lockout was switched on (UTC, ISO 8601). Informational.",
        [EndsAtKey] =
            "When the lockout lifts on its own (UTC, ISO 8601). Blank means it stays on until somebody turns it off. "
            + "Past this time the switch stops applying even while it is still enabled.",
        [UpdatedByKey] = "Who last changed the switch."
    };

    private bool ReadBool(IReadOnlyDictionary<string, string?> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            _logger.LogWarning(
                "{Key} is {Value}, which is not true or false. Treating the mobile maintenance lockout as off.",
                key, raw);
            return false;
        }

        return parsed;
    }

    private MaintenanceScope ReadScope(IReadOnlyDictionary<string, string?> values)
    {
        if (!values.TryGetValue(ScopeKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return MaintenanceScope.Transactions;
        }

        if (!Enum.TryParse<MaintenanceScope>(raw.Trim(), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            _logger.LogWarning(
                "{Key} is {Value}, which is not a known scope. Falling back to {Fallback}.",
                ScopeKey, raw, MaintenanceScope.Transactions);
            return MaintenanceScope.Transactions;
        }

        return parsed;
    }

    private static string? ReadString(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    private DateTime? ReadDate(IReadOnlyDictionary<string, string?> values, string key)
    {
        var raw = ReadString(values, key);
        if (raw is null)
        {
            return null;
        }

        // RoundtripKind alone, and not with AdjustToUniversal: the two are mutually exclusive and
        // combining them throws. These values are written with "O" from a UTC DateTime, so the
        // trailing Z gives the parse a Utc kind and the normalisation below is only a guard against
        // a row somebody edited by hand.
        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            _logger.LogWarning("{Key} is {Value}, which is not a date. Ignoring it.", key, raw);
            return null;
        }

        // A hand-edited row with no offset is read as UTC rather than as server local time, which
        // is what the rest of this feature stores and what the settings screen sends.
        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// The app keys, dropping anything the catalogue does not recognise.
    /// </summary>
    /// <remarks>
    /// A key that no longer exists must not survive here: it would narrow the lockout to an app
    /// that cannot match, which reads on the screen as "maintenance is on" while every phone keeps
    /// trading. Dropping it is the safe direction — the list ends up empty, and an empty list
    /// covers everything.
    /// </remarks>
    private List<string> ReadAppIds(IReadOnlyDictionary<string, string?> values)
    {
        var raw = ReadString(values, AppsKey);
        if (raw is null)
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw);
            if (parsed is null)
            {
                return [];
            }

            return [.. parsed
                .Select(entry => MobileVersionPolicyAppCatalog.TryResolvePolicyKey(entry, out var key) ? key : null)
                .Where(key => key is not null)
                .Select(key => key!)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Key} is not a JSON array of strings. Treating the lockout as covering every app.", AppsKey);
            return [];
        }
    }

    /// <summary>
    /// The rows as they should be seeded, so an operator meets a switch that exists rather than a
    /// key they have to guess. Same reasoning as <c>VanSalesOrderingPolicy.DescribeDefaultRows</c>.
    /// </summary>
    public static IReadOnlyList<SystemConfigEntity> DescribeDefaultRows(DateTime nowUtc) =>
        [.. Describe(MaintenanceState.Off).Select(row => NewRow(row.Key, row.Value, row.Type, nowUtc))];
}
