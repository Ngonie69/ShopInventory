namespace ShopInventory.Features.AppVersion;

public sealed record MobileVersionPolicyEvaluation(
    string Status,
    string? CurrentVersion,
    string LatestVersion,
    string RecommendedVersion,
    string MinimumSupportedVersion,
    string DownloadUrl,
    string? ReleaseNotes,
    string? Message,
    bool ShouldForceUpgrade,
    DateTime CheckedAtUtc,
    bool PolicyApplies,
    bool HasValidVersionMetadata,
    bool RequireHeaders);