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
}
