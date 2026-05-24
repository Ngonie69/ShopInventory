using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.AppVersion.Commands.UpdateMobileVersionPolicySettings;

public sealed class UpdateMobileVersionPolicySettingsHandler(
    IAuditService auditService,
    INotificationService notificationService,
    IOptionsMonitor<MobileVersionPolicyOptions> optionsMonitor,
    ILogger<UpdateMobileVersionPolicySettingsHandler> logger
) : IRequestHandler<UpdateMobileVersionPolicySettingsCommand, ErrorOr<UpdateMobileVersionPolicySettingsResult>>
{
    public async Task<ErrorOr<UpdateMobileVersionPolicySettingsResult>> Handle(
        UpdateMobileVersionPolicySettingsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!MobileVersionPolicyAppCatalog.TryResolvePolicyKey(command.Request.AppId, out var policyKey))
            {
                return Errors.AppVersion.SettingsUpdateFailed("Unsupported mobile app policy.");
            }

            var appDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey);

            logger.LogWarning("Mobile version policy settings update requested by {User} for {App}", command.UserName, appDisplayName);
            var currentPolicy = NormalizeSettings(optionsMonitor.CurrentValue, policyKey);

            var webConfigPath = Path.Combine(AppContext.BaseDirectory, "web.config");
            if (!File.Exists(webConfigPath))
                return Errors.AppVersion.SettingsUpdateFailed("web.config not found. Settings can only be updated on IIS deployments.");

            var xml = new System.Xml.XmlDocument();
            xml.Load(webConfigPath);

            var envVarsNode = xml.SelectSingleNode("//aspNetCore/environmentVariables");
            if (envVarsNode == null)
                return Errors.AppVersion.SettingsUpdateFailed("Could not find environmentVariables section in web.config.");

            var request = command.Request;
            var updatedPolicy = NormalizeSettings(request, policyKey);
            var envVarPrefix = $"MobileVersionPolicy__Apps__{policyKey}__";

            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "Enabled", updatedPolicy.Enabled.ToString().ToLowerInvariant());
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "RequireHeaders", updatedPolicy.RequireHeaders.ToString().ToLowerInvariant());
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "LatestVersion", updatedPolicy.LatestVersion);
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "RecommendedVersion", updatedPolicy.RecommendedVersion);
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "MinimumSupportedVersion", updatedPolicy.MinimumSupportedVersion);
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "GooglePlayUrl", updatedPolicy.GooglePlayUrl);
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "ReleaseNotes", updatedPolicy.ReleaseNotes);
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "WarnMessage", updatedPolicy.WarnMessage);
            SetEnvironmentVariable(envVarsNode, xml, envVarPrefix + "BlockMessage", updatedPolicy.BlockMessage);

            xml.Save(webConfigPath);

            logger.LogInformation("Mobile version policy settings updated in web.config by {User} for {App}", command.UserName, appDisplayName);

            try
            {
                await auditService.LogAsync(
                    AuditActions.UpdateMobileVersionPolicy,
                    "MobileVersionPolicy",
                    policyKey,
                    $"{appDisplayName} mobile version policy updated by {command.UserName}",
                    true);
            }
            catch
            {
            }

            try
            {
                if (ShouldNotifyUsersOfAppUpdate(currentPolicy, updatedPolicy))
                {
                    foreach (var notificationRequest in BuildAppUpdateNotifications(policyKey, updatedPolicy))
                    {
                        await notificationService.CreateNotificationAsync(notificationRequest, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish mobile app update notification for {App}", appDisplayName);
            }

            return new UpdateMobileVersionPolicySettingsResult(
                $"{appDisplayName} version policy settings updated successfully. App pool restart may be required for changes to take effect.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update mobile version policy settings");
            return Errors.AppVersion.SettingsUpdateFailed(ex.Message);
        }
    }

    private static void SetEnvironmentVariable(System.Xml.XmlNode envVarsNode, System.Xml.XmlDocument xml, string name, string value)
    {
        var existing = envVarsNode.SelectSingleNode($"environmentVariable[@name='{name}']");
        if (existing != null)
        {
            existing.Attributes!["value"]!.Value = value;
        }
        else
        {
            var newNode = xml.CreateElement("environmentVariable");
            newNode.SetAttribute("name", name);
            newNode.SetAttribute("value", value);
            envVarsNode.AppendChild(newNode);
        }
    }

    private static MobileVersionPolicySettingsDto NormalizeSettings(MobileVersionPolicyOptions options, string policyKey)
    {
        if (options.Apps.TryGetValue(policyKey, out var profile))
        {
            return NormalizeSettings(profile, policyKey);
        }

        return new MobileVersionPolicySettingsDto
        {
            AppId = policyKey,
            AppDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey),
            Enabled = options.Enabled,
            RequireHeaders = options.RequireHeaders,
            LatestVersion = NormalizeSettingValue(options.LatestVersion),
            RecommendedVersion = NormalizeSettingValue(options.RecommendedVersion),
            MinimumSupportedVersion = NormalizeSettingValue(options.MinimumSupportedVersion),
            GooglePlayUrl = NormalizeSettingValue(options.GooglePlayUrl),
            ReleaseNotes = NormalizeSettingValue(options.ReleaseNotes),
            WarnMessage = NormalizeSettingValue(options.WarnMessage),
            BlockMessage = NormalizeSettingValue(options.BlockMessage)
        };
    }

    private static MobileVersionPolicySettingsDto NormalizeSettings(MobileVersionPolicyProfileOptions profile, string policyKey) => new()
    {
        AppId = policyKey,
        AppDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey),
        Enabled = profile.Enabled,
        RequireHeaders = profile.RequireHeaders,
        LatestVersion = NormalizeSettingValue(profile.LatestVersion),
        RecommendedVersion = NormalizeSettingValue(profile.RecommendedVersion),
        MinimumSupportedVersion = NormalizeSettingValue(profile.MinimumSupportedVersion),
        GooglePlayUrl = NormalizeSettingValue(profile.GooglePlayUrl),
        ReleaseNotes = NormalizeSettingValue(profile.ReleaseNotes),
        WarnMessage = NormalizeSettingValue(profile.WarnMessage),
        BlockMessage = NormalizeSettingValue(profile.BlockMessage)
    };

    private static MobileVersionPolicySettingsDto NormalizeSettings(UpdateMobileVersionPolicySettingsRequest request, string policyKey) => new()
    {
        AppId = policyKey,
        AppDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey),
        Enabled = request.Enabled,
        RequireHeaders = request.RequireHeaders,
        LatestVersion = NormalizeSettingValue(request.LatestVersion),
        RecommendedVersion = NormalizeSettingValue(request.RecommendedVersion),
        MinimumSupportedVersion = NormalizeSettingValue(request.MinimumSupportedVersion),
        GooglePlayUrl = NormalizeSettingValue(request.GooglePlayUrl),
        ReleaseNotes = NormalizeSettingValue(request.ReleaseNotes),
        WarnMessage = NormalizeSettingValue(request.WarnMessage),
        BlockMessage = NormalizeSettingValue(request.BlockMessage)
    };

    private static bool ShouldNotifyUsersOfAppUpdate(
        MobileVersionPolicySettingsDto currentPolicy,
        MobileVersionPolicySettingsDto updatedPolicy)
    {
        if (!updatedPolicy.Enabled || string.IsNullOrWhiteSpace(updatedPolicy.LatestVersion))
        {
            return false;
        }

        return currentPolicy.Enabled != updatedPolicy.Enabled ||
               !string.Equals(currentPolicy.LatestVersion, updatedPolicy.LatestVersion, StringComparison.Ordinal) ||
               !string.Equals(currentPolicy.RecommendedVersion, updatedPolicy.RecommendedVersion, StringComparison.Ordinal) ||
               !string.Equals(currentPolicy.MinimumSupportedVersion, updatedPolicy.MinimumSupportedVersion, StringComparison.Ordinal) ||
               !string.Equals(currentPolicy.GooglePlayUrl, updatedPolicy.GooglePlayUrl, StringComparison.Ordinal) ||
               !string.Equals(currentPolicy.ReleaseNotes, updatedPolicy.ReleaseNotes, StringComparison.Ordinal) ||
               !string.Equals(currentPolicy.WarnMessage, updatedPolicy.WarnMessage, StringComparison.Ordinal) ||
               !string.Equals(currentPolicy.BlockMessage, updatedPolicy.BlockMessage, StringComparison.Ordinal);
    }

    private static IEnumerable<CreateNotificationRequest> BuildAppUpdateNotifications(string policyKey, MobileVersionPolicySettingsDto policy)
    {
        var hasMandatoryUpdate = !string.IsNullOrWhiteSpace(policy.MinimumSupportedVersion);
        var targetVersion = !string.IsNullOrWhiteSpace(policy.RecommendedVersion)
            ? policy.RecommendedVersion
            : policy.LatestVersion;
        var appDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey);
        var title = hasMandatoryUpdate
            ? $"Update {appDisplayName} to continue"
            : $"{appDisplayName} update available";
        var message = hasMandatoryUpdate
            ? $"{appDisplayName} version {policy.LatestVersion} is available. Minimum supported version is {policy.MinimumSupportedVersion}. Update the app to continue using mobile features."
            : $"{appDisplayName} version {targetVersion} is available. Update the app to get the latest changes.";

        if (!string.IsNullOrWhiteSpace(policy.ReleaseNotes))
        {
            message = $"{message} What's new: {Truncate(policy.ReleaseNotes, 220)}";
        }

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["latestVersion"] = policy.LatestVersion,
            ["recommendedVersion"] = policy.RecommendedVersion,
            ["minimumSupportedVersion"] = policy.MinimumSupportedVersion,
            ["downloadUrl"] = policy.GooglePlayUrl,
            ["releaseNotes"] = policy.ReleaseNotes,
            ["warnMessage"] = policy.WarnMessage,
            ["blockMessage"] = policy.BlockMessage,
            ["enabled"] = policy.Enabled ? "true" : "false",
            ["appId"] = policyKey,
            ["appDisplayName"] = appDisplayName
        };

        foreach (var targetRole in MobileVersionPolicyAppCatalog.GetNotificationAudienceRoles(policyKey))
        {
            yield return new CreateNotificationRequest
            {
                Title = title,
                Message = message,
                Type = hasMandatoryUpdate ? "Warning" : "Info",
                Category = "AppVersion",
                EntityType = "AppVersion",
                EntityId = string.IsNullOrWhiteSpace(policy.LatestVersion) ? policyKey : policy.LatestVersion,
                TargetRole = targetRole,
                Data = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    private static string NormalizeSettingValue(string? value) => value?.Trim() ?? string.Empty;

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 3)] + "...";
    }
}