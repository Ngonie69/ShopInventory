using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.AppVersion.Commands.UpdateMobileVersionPolicySettings;

public sealed class UpdateMobileVersionPolicySettingsHandler(
    IAuditService auditService,
    ILogger<UpdateMobileVersionPolicySettingsHandler> logger
) : IRequestHandler<UpdateMobileVersionPolicySettingsCommand, ErrorOr<UpdateMobileVersionPolicySettingsResult>>
{
    public async Task<ErrorOr<UpdateMobileVersionPolicySettingsResult>> Handle(
        UpdateMobileVersionPolicySettingsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogWarning("Mobile version policy settings update requested by {User}", command.UserName);

            var webConfigPath = Path.Combine(AppContext.BaseDirectory, "web.config");
            if (!File.Exists(webConfigPath))
                return Errors.AppVersion.SettingsUpdateFailed("web.config not found. Settings can only be updated on IIS deployments.");

            var xml = new System.Xml.XmlDocument();
            xml.Load(webConfigPath);

            var envVarsNode = xml.SelectSingleNode("//aspNetCore/environmentVariables");
            if (envVarsNode == null)
                return Errors.AppVersion.SettingsUpdateFailed("Could not find environmentVariables section in web.config.");

            var request = command.Request;
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__Enabled", request.Enabled.ToString().ToLowerInvariant());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__RequireHeaders", request.RequireHeaders.ToString().ToLowerInvariant());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__LatestVersion", request.LatestVersion.Trim());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__RecommendedVersion", request.RecommendedVersion.Trim());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__MinimumSupportedVersion", request.MinimumSupportedVersion.Trim());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__GooglePlayUrl", request.GooglePlayUrl.Trim());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__ReleaseNotes", request.ReleaseNotes.Trim());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__WarnMessage", request.WarnMessage.Trim());
            SetEnvironmentVariable(envVarsNode, xml, "MobileVersionPolicy__BlockMessage", request.BlockMessage.Trim());

            xml.Save(webConfigPath);

            logger.LogInformation("Mobile version policy settings updated in web.config by {User}", command.UserName);

            try
            {
                await auditService.LogAsync(
                    AuditActions.UpdateMobileVersionPolicy,
                    "MobileVersionPolicy",
                    null,
                    $"Mobile version policy updated by {command.UserName}",
                    true);
            }
            catch
            {
            }

            return new UpdateMobileVersionPolicySettingsResult(
                "Mobile version policy settings updated successfully. App pool restart may be required for changes to take effect.");
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
}