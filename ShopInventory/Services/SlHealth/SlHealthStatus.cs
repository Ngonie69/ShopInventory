namespace ShopInventory.Services.SlHealth;

/// <summary>
/// SL Health's answer to <c>GET /api/integration/status</c>: the state of the SAP Service Layer as of
/// its last check.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors version 1 of SL Health's <c>AppStatus</c>. Fields are only ever added there without the
/// version rising, so anything not named here is skipped on reading.
/// </para>
/// <para>
/// The answer also carries the certificate the Service Layer presents. It is left out here on purpose.
/// The thumbprints this API trusts come from <c>SAP:AllowedServerCertificateThumbprints</c> and nowhere
/// else: pinning one because SL Health reported it would be trusting whatever answers on the port.
/// </para>
/// </remarks>
/// <param name="Version">Of the shape. Raised only by a change that breaks a reader.</param>
/// <param name="CheckedAt">When SL Health last checked, in Unix milliseconds.</param>
/// <param name="Stale">SL Health's checks have stopped, so the rest describes an earlier moment.</param>
/// <param name="Level"><c>Healthy</c>, <c>Warning</c>, <c>Critical</c>, <c>Down</c> or <c>Unknown</c>.</param>
/// <param name="Label">The level as SL Health shows it to people, for example <c>EXTREME LOAD</c>.</param>
/// <param name="Since">When the level became what it is, in Unix milliseconds.</param>
/// <param name="Reasons">Why the level is what it is, in the words SL Health's alerts use.</param>
/// <param name="Advice"><c>Proceed</c>, <c>HoldBackground</c>, <c>Unavailable</c> or <c>Unknown</c>.</param>
/// <param name="Ping">Whether the Service Layer answered SL Health's ping.</param>
/// <param name="Login">Whether SL Health could log in. Null when it does not test logins.</param>
/// <param name="Queries">The test queries SL Health runs through the Service Layer.</param>
/// <param name="ServerTools">Whether Server Tools (SLD, licensing) answered. New logins fail without it.</param>
/// <param name="Maintenance">The planned window in force, or null.</param>
/// <param name="Restart">The restart SL Health is running, or null.</param>
public sealed record SlHealthStatus(
    int Version,
    long? CheckedAt,
    bool Stale,
    string? Level,
    string? Label,
    long? Since,
    IReadOnlyList<string>? Reasons,
    string? Advice,
    SlHealthCheck? Ping,
    SlHealthCheck? Login,
    IReadOnlyList<SlHealthCheck>? Queries,
    SlHealthCheck? ServerTools,
    SlHealthMaintenance? Maintenance,
    SlHealthRestart? Restart)
{
    /// <summary>The newest shape this reader understands.</summary>
    public const int SupportedVersion = 1;

    /// <summary>What SL Health advises, with anything this reader does not recognise read as Unknown.</summary>
    public SlHealthAdvice AdviceValue => Advice?.Trim().ToUpperInvariant() switch
    {
        "PROCEED" => SlHealthAdvice.Proceed,
        "HOLDBACKGROUND" => SlHealthAdvice.HoldBackground,
        "UNAVAILABLE" => SlHealthAdvice.Unavailable,
        _ => SlHealthAdvice.Unknown
    };

    /// <summary>
    /// SL Health itself cannot log in to SAP or cannot run its test queries, or Server Tools, which every
    /// new login depends on, is not answering.
    /// </summary>
    public bool HasFailingCheck =>
        Login is { Ok: false }
        || ServerTools is { Ok: false }
        || (Queries?.Any(query => query is { Ok: false }) ?? false);
}

/// <summary>One of SL Health's own checks against the Service Layer.</summary>
public sealed record SlHealthCheck(string? Name, bool Ok, double? Ms, string? Error);

/// <param name="Reason">What the planned window is for, as the person who scheduled it wrote it.</param>
/// <param name="Until">When the window ends, in Unix milliseconds.</param>
public sealed record SlHealthMaintenance(string? Reason, long Until);

/// <param name="Target"><c>ServiceLayer</c> or <c>ServerTools</c>.</param>
/// <param name="StartedAt">When the restart was started from SL Health, in Unix milliseconds.</param>
public sealed record SlHealthRestart(string? Target, long StartedAt)
{
    /// <summary>Open sessions survive a Server Tools restart; only this one takes the Service Layer away.</summary>
    public bool IsServiceLayer => string.Equals(Target, "ServiceLayer", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What SL Health advises an application to do with a Service Layer call it is about to make.</summary>
public enum SlHealthAdvice
{
    /// <summary>SL Health cannot say. Carry on as if it were not there.</summary>
    Unknown,

    Proceed,

    /// <summary>EXTREME LOAD. Nothing in this API acts on it yet.</summary>
    HoldBackground,

    /// <summary>DOWN, or being restarted: a call will fail or hang.</summary>
    Unavailable
}
