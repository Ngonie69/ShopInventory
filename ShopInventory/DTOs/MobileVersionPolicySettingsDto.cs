namespace ShopInventory.DTOs;

public sealed class MobileVersionPolicySettingsDto
{
    public bool Enabled { get; set; }
    public bool RequireHeaders { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string RecommendedVersion { get; set; } = string.Empty;
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public string GooglePlayUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string WarnMessage { get; set; } = string.Empty;
    public string BlockMessage { get; set; } = string.Empty;
}