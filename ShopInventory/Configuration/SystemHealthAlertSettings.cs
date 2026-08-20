namespace ShopInventory.Configuration;

public sealed class SystemHealthAlertSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Email addresses that receive system failure / degraded alerts.</summary>
    public List<string> AlertRecipients { get; set; } = new();

    /// <summary>Minimum minutes between repeated alerts for the same failure state.</summary>
    public int AlertCooldownMinutes { get; set; } = 15;

    /// <summary>How often the background service polls health checks, in minutes.</summary>
    public int CheckIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// How many consecutive polls must see the same Degraded condition before it is alerted on.
    /// 1 alerts on the first observation.
    /// </summary>
    /// <remarks>
    /// Degraded is frequently a passing thing. On 2026-08-20 SAP wobbled for five minutes — three
    /// transient errors and a BadGateway, all absorbed by the price-list fallback, no user request
    /// failed — and it produced an email and an org-wide push at 08:11, then a second email and a
    /// second push at 08:16 when it cleared. Four messages for a blip nobody would otherwise have
    /// noticed is how recipients learn to ignore the alerts.
    /// <para>
    /// Unhealthy is never held back by this. A hard failure is not a blip, and waiting a poll to
    /// confirm it costs real minutes. This applies to Degraded alone.
    /// </para>
    /// </remarks>
    public int DegradedConfirmations { get; set; } = 2;
}
