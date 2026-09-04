using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RateLimit;

/// <inheritdoc />
public sealed class RateLimitConfigStore : IRateLimitConfigStore
{
    /// <summary>How long a snapshot is served before a read triggers a refresh.</summary>
    /// <remarks>
    /// The window over which instances converge after a change. Long enough that the hot path is
    /// nowhere near the database, short enough that an operator throttling a misbehaving client
    /// does not sit wondering whether it worked.
    /// </remarks>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    public const string PermitLimitKey = "RateLimit.PermitLimit";
    public const string WindowSecondsKey = "RateLimit.WindowSeconds";
    public const string BlockDurationMinutesKey = "RateLimit.BlockDurationMinutes";
    public const string EnableIpRateLimitingKey = "RateLimit.EnableIpRateLimiting";
    public const string IpWhitelistKey = "RateLimit.IpWhitelist";
    public const string ApiKeyWhitelistKey = "RateLimit.ApiKeyWhitelist";

    /// <summary>
    /// Floors, not preferences. ASP.NET Core throws when a window limiter is built with a permit
    /// limit below 1 or a window of zero, and that throw happens inside the partition factory - on
    /// the request path, for every request. A stored value outside these bounds is ignored in
    /// favour of the configured one, so a bad row cannot take the API down.
    /// </summary>
    public const int MinPermitLimit = 1;
    public const int MaxPermitLimit = 1_000_000;
    public const int MinWindowSeconds = 1;
    public const int MaxWindowSeconds = 86_400;
    public const int MinBlockDurationMinutes = 0;
    public const int MaxBlockDurationMinutes = 43_200;

    private static readonly string[] AllKeys =
    [
        PermitLimitKey, WindowSecondsKey, BlockDurationMinutesKey,
        EnableIpRateLimitingKey, IpWhitelistKey, ApiKeyWhitelistKey
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RateLimitConfigStore> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>What configuration says, before any stored override.</summary>
    private readonly RateLimitSettings _configured;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile Snapshot _snapshot;

    private sealed record Snapshot(RateLimitSettings Settings, DateTimeOffset LoadedAt);

    public RateLimitConfigStore(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<RateLimitConfigStore> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _configured = configuration.GetSection("RateLimit").Get<RateLimitSettings>() ?? new RateLimitSettings();

        // Seeded from configuration so the very first request is served without touching the
        // database. Stamped stale, so that request also starts the first load.
        _snapshot = new Snapshot(_configured.Clone(), DateTimeOffset.MinValue);
    }

    /// <inheritdoc />
    public RateLimitSettings Current
    {
        get
        {
            var snapshot = _snapshot;
            if (_timeProvider.GetUtcNow() - snapshot.LoadedAt >= RefreshInterval)
            {
                BeginRefresh();
            }

            return snapshot.Settings;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(RateLimitSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await context.SystemConfigs
            .Where(config => AllKeys.Contains(config.Key))
            .ToListAsync(cancellationToken);

        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value, type) in Describe(settings))
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

        // Apply here at once rather than waiting out the refresh interval: the operator who made
        // the change is the one most likely to check whether it took.
        _snapshot = new Snapshot(Merge(settings), _timeProvider.GetUtcNow());

        _logger.LogInformation(
            "Rate limit configuration updated: PermitLimit={PermitLimit}, WindowSeconds={WindowSeconds}, "
            + "BlockDurationMinutes={BlockDurationMinutes}, IpRateLimiting={Enabled}, "
            + "IpWhitelist={IpCount}, ApiKeyWhitelist={KeyCount}",
            settings.PermitLimit, settings.WindowSeconds, settings.BlockDurationMinutes,
            settings.EnableIpRateLimiting, settings.IpWhitelist.Count, settings.ApiKeyWhitelist.Count);
    }

    /// <inheritdoc />
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(cancellationToken);
        _snapshot = new Snapshot(loaded, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Refresh off the request path. A failure leaves the previous snapshot in place - limits that
    /// are a few seconds out of date beat an outage, and beat hammering a database that is already
    /// unwell once per request.
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
                _logger.LogWarning(ex, "Could not reload rate limit configuration; keeping the current limits.");

                // Stamp the attempt so a database that is down is retried on the interval rather
                // than on every single request.
                _snapshot = new Snapshot(_snapshot.Settings, _timeProvider.GetUtcNow());
            }
            finally
            {
                _refreshGate.Release();
            }
        });
    }

    private async Task<RateLimitSettings> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await context.SystemConfigs
            .AsNoTracking()
            .Where(config => AllKeys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(cancellationToken);

        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);

        var settings = _configured.Clone();
        settings.PermitLimit = ReadInt(values, PermitLimitKey, settings.PermitLimit, MinPermitLimit, MaxPermitLimit);
        settings.WindowSeconds = ReadInt(values, WindowSecondsKey, settings.WindowSeconds, MinWindowSeconds, MaxWindowSeconds);
        settings.BlockDurationMinutes = ReadInt(
            values, BlockDurationMinutesKey, settings.BlockDurationMinutes, MinBlockDurationMinutes, MaxBlockDurationMinutes);
        settings.EnableIpRateLimiting = ReadBool(values, EnableIpRateLimitingKey, settings.EnableIpRateLimiting);
        settings.IpWhitelist = ReadList(values, IpWhitelistKey, settings.IpWhitelist);
        settings.ApiKeyWhitelist = ReadList(values, ApiKeyWhitelistKey, settings.ApiKeyWhitelist);
        return settings;
    }

    /// <summary>
    /// The settable fields over the configured ones, so a field the API does not expose keeps
    /// coming from configuration instead of being silently reset to a default.
    /// </summary>
    private RateLimitSettings Merge(RateLimitSettings settings)
    {
        var merged = _configured.Clone();
        merged.PermitLimit = settings.PermitLimit;
        merged.WindowSeconds = settings.WindowSeconds;
        merged.BlockDurationMinutes = settings.BlockDurationMinutes;
        merged.EnableIpRateLimiting = settings.EnableIpRateLimiting;
        merged.IpWhitelist = [.. settings.IpWhitelist];
        merged.ApiKeyWhitelist = [.. settings.ApiKeyWhitelist];
        return merged;
    }

    private static IEnumerable<(string Key, string Value, string Type)> Describe(RateLimitSettings settings) =>
    [
        (PermitLimitKey, settings.PermitLimit.ToString(CultureInfo.InvariantCulture), "int"),
        (WindowSecondsKey, settings.WindowSeconds.ToString(CultureInfo.InvariantCulture), "int"),
        (BlockDurationMinutesKey, settings.BlockDurationMinutes.ToString(CultureInfo.InvariantCulture), "int"),
        (EnableIpRateLimitingKey, settings.EnableIpRateLimiting ? "true" : "false", "bool"),
        (IpWhitelistKey, JsonSerializer.Serialize(settings.IpWhitelist), "json"),
        (ApiKeyWhitelistKey, JsonSerializer.Serialize(settings.ApiKeyWhitelist), "json")
    ];

    private static SystemConfigEntity NewRow(string key, string value, string type, DateTime now) => new()
    {
        Key = key,
        Value = value,
        ValueType = type,
        Category = "RateLimit",
        Description = Descriptions.TryGetValue(key, out var text) ? text : null,
        IsEditable = true,
        // The API key whitelist names credentials, even though it only exempts them from
        // throttling. Marked so an operator screen listing SystemConfigs does not print them.
        IsSensitive = key == ApiKeyWhitelistKey,
        UpdatedAt = now
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [PermitLimitKey] = "Requests allowed per client per window before the API answers 429. Applies to the 'fixed' and 'api' policies; the stricter auth-endpoint limit is a deployment setting.",
        [WindowSecondsKey] = "Length of the rate limit window in seconds.",
        [BlockDurationMinutesKey] = "How long /api/RateLimit blocks a client for. Does not affect the ASP.NET Core limiter, which does not block.",
        [EnableIpRateLimitingKey] = "Whether unauthenticated callers are limited per IP. False lumps them into one 'anonymous' bucket, which is one shared limit for the whole internet.",
        [IpWhitelistKey] = "JSON array of IP addresses exempt from rate limiting entirely.",
        [ApiKeyWhitelistKey] = "JSON array of API keys exempt from rate limiting. Exempts from throttling only; grants no access."
    };

    private int ReadInt(IReadOnlyDictionary<string, string?> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            _logger.LogWarning(
                "{Key} is {Value}, which is not a whole number. Falling back to {Fallback}.", key, raw, fallback);
            return fallback;
        }

        if (parsed < min || parsed > max)
        {
            _logger.LogWarning(
                "{Key} is {Value}, outside {Min}-{Max}. Falling back to {Fallback}.", key, parsed, min, max, fallback);
            return fallback;
        }

        return parsed;
    }

    private bool ReadBool(IReadOnlyDictionary<string, string?> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            _logger.LogWarning(
                "{Key} is {Value}, which is not true or false. Falling back to {Fallback}.", key, raw, fallback);
            return fallback;
        }

        return parsed;
    }

    private List<string> ReadList(IReadOnlyDictionary<string, string?> values, string key, List<string> fallback)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw);
            if (parsed is null)
            {
                return fallback;
            }

            return [.. parsed
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Key} is not a JSON array of strings. Falling back to the configured list.", key);
            return fallback;
        }
    }

    /// <summary>
    /// The rows as they should be seeded, so an operator can edit a setting that exists rather than
    /// guess a key. Same reasoning as <c>VanSalesOrderingPolicy.DescribeDefaultRows</c>.
    /// </summary>
    public IReadOnlyList<SystemConfigEntity> DescribeDefaultRows(DateTime nowUtc) =>
        [.. Describe(_configured).Select(row => NewRow(row.Key, row.Value, row.Type, nowUtc))];
}
