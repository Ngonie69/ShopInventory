namespace ShopInventory.Configuration;

public class MobileVersionPolicyOptions
{
    public const string SectionName = "MobileVersionPolicy";

    public const string DefaultPendingStatusAwareVersion = "1.0.4";

    public bool Enabled { get; set; }

    public bool RequireHeaders { get; set; } = true;

    public string LatestVersion { get; set; } = string.Empty;

    public string RecommendedVersion { get; set; } = string.Empty;

    public string MinimumSupportedVersion { get; set; } = string.Empty;

    public string PendingStatusAwareVersion { get; set; } = DefaultPendingStatusAwareVersion;

    public string GooglePlayUrl { get; set; } = string.Empty;

    public string? ReleaseNotes { get; set; }

    public string? WarnMessage { get; set; }

    public string? BlockMessage { get; set; }
}