using FluentValidation;
using ShopInventory.DTOs;

namespace ShopInventory.Features.AppVersion.Commands.UpdateMobileVersionPolicySettings;

public sealed class UpdateMobileVersionPolicySettingsValidator : AbstractValidator<UpdateMobileVersionPolicySettingsCommand>
{
    public UpdateMobileVersionPolicySettingsValidator()
    {
        RuleFor(x => x.Request.LatestVersion)
            .Must(BeEmptyOrValidVersion)
            .WithMessage("Latest version must be a valid version like 1.0.2.");

        RuleFor(x => x.Request.RecommendedVersion)
            .Must(BeEmptyOrValidVersion)
            .WithMessage("Recommended version must be a valid version like 1.0.2.");

        RuleFor(x => x.Request.MinimumSupportedVersion)
            .Must(BeEmptyOrValidVersion)
            .WithMessage("Minimum supported version must be a valid version like 1.0.2.");

        RuleFor(x => x.Request.GooglePlayUrl)
            .Must(BeEmptyOrAbsoluteUrl)
            .WithMessage("Google Play URL must be an absolute URL.");

        RuleFor(x => x.Request)
            .Must(HaveLatestVersionWhenNeeded)
            .WithMessage("Latest version is required when the mobile version policy is enabled or when other version fields are set.");

        RuleFor(x => x.Request)
            .Must(HaveValidVersionOrdering)
            .WithMessage("Minimum supported version must be less than or equal to recommended version, and recommended version must be less than or equal to latest version.");
    }

    private static bool BeEmptyOrValidVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return Version.TryParse(value.Trim(), out _);
    }

    private static bool BeEmptyOrAbsoluteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out _);
    }

    private static bool HaveLatestVersionWhenNeeded(UpdateMobileVersionPolicySettingsRequest request)
    {
        var needsLatestVersion = request.Enabled
            || !string.IsNullOrWhiteSpace(request.RecommendedVersion)
            || !string.IsNullOrWhiteSpace(request.MinimumSupportedVersion);

        if (!needsLatestVersion)
            return true;

        return Version.TryParse(request.LatestVersion?.Trim(), out _);
    }

    private static bool HaveValidVersionOrdering(UpdateMobileVersionPolicySettingsRequest request)
    {
        if (!Version.TryParse(request.LatestVersion?.Trim(), out var latestVersion))
            return true;

        var recommendedVersion = Version.TryParse(request.RecommendedVersion?.Trim(), out var parsedRecommended)
            ? parsedRecommended
            : latestVersion;

        var minimumSupportedVersion = Version.TryParse(request.MinimumSupportedVersion?.Trim(), out var parsedMinimum)
            ? parsedMinimum
            : recommendedVersion;

        return minimumSupportedVersion.CompareTo(recommendedVersion) <= 0
               && recommendedVersion.CompareTo(latestVersion) <= 0;
    }
}