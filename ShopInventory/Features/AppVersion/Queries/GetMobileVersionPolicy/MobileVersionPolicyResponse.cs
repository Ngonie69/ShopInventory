namespace ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicy;

public sealed record MobileVersionPolicyResponse(
    string Status,
    string? CurrentVersion,
    string LatestVersion,
    string RecommendedVersion,
    string MinimumSupportedVersion,
    string DownloadUrl,
    string? ReleaseNotes,
    string? Message,
    bool ShouldForceUpgrade,
    DateTime CheckedAtUtc);