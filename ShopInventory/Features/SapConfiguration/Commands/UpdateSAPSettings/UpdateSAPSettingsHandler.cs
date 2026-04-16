using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapConfiguration.Commands.UpdateSAPSettings;

public sealed class UpdateSAPSettingsHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    ILogger<UpdateSAPSettingsHandler> logger
) : IRequestHandler<UpdateSAPSettingsCommand, ErrorOr<UpdateSAPSettingsResult>>
{
    public async Task<ErrorOr<UpdateSAPSettingsResult>> Handle(
        UpdateSAPSettingsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogWarning("SAP settings update requested by {User}", command.UserName);

            var request = command.Request;

            var webConfigPath = Path.Combine(AppContext.BaseDirectory, "web.config");
            if (!File.Exists(webConfigPath))
                return Errors.SAPSettings.UpdateFailed("web.config not found. Settings can only be updated on IIS deployments.");

            var xml = new System.Xml.XmlDocument();
            xml.Load(webConfigPath);

            var envVarsNode = xml.SelectSingleNode("//aspNetCore/environmentVariables");
            if (envVarsNode == null)
                return Errors.SAPSettings.UpdateFailed("Could not find environmentVariables section in web.config.");

            SetEnvironmentVariable(envVarsNode, xml, "SAP__ServiceLayerUrl", request.ServiceLayerUrl);
            SetEnvironmentVariable(envVarsNode, xml, "SAP__CompanyDB", request.CompanyDB);
            SetEnvironmentVariable(envVarsNode, xml, "SAP__Username", request.UserName);
            SetEnvironmentVariable(envVarsNode, xml, "SAP__Password", request.Password);

            xml.Save(webConfigPath);

            logger.LogInformation("SAP settings updated in web.config by {User}", command.UserName);

            try { await auditService.LogAsync(AuditActions.UpdateSAPSettings, "SAPSettings", null, $"SAP settings updated by {command.UserName}", true); } catch { }

            if (request.TestConnection)
            {
                try
                {
                    var connected = await sapClient.TestConnectionWithCredentialsAsync(
                        request.ServiceLayerUrl, request.CompanyDB, request.UserName, request.Password,
                        cancellationToken);
                    return new UpdateSAPSettingsResult(
                        connected
                            ? "SAP settings updated and connection test successful. App pool restart may be required for changes to fully take effect."
                            : "SAP settings updated but connection test failed. Verify your credentials.",
                        connected);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SAP connection test failed after settings update");
                    return new UpdateSAPSettingsResult(
                        $"SAP settings updated but connection test failed: {ex.Message}. App pool restart may be required.",
                        false);
                }
            }

            return new UpdateSAPSettingsResult(
                "SAP settings updated successfully. App pool restart may be required for changes to take effect.",
                null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update SAP settings");
            return Errors.SAPSettings.UpdateFailed(ex.Message);
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
