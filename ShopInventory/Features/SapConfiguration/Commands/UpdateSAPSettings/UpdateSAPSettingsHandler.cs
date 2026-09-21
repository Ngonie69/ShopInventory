using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapConfiguration.Commands.UpdateSAPSettings;

public sealed class UpdateSAPSettingsHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    IOptionsMonitor<SAPSettings> sapSettings,
    ILogger<UpdateSAPSettingsHandler> logger
) : IRequestHandler<UpdateSAPSettingsCommand, ErrorOr<UpdateSAPSettingsResult>>
{
    /// <summary>The web.config the settings are written to. Settable so tests can point it at a temp file.</summary>
    internal string WebConfigPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "web.config");

    public async Task<ErrorOr<UpdateSAPSettingsResult>> Handle(
        UpdateSAPSettingsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogWarning("SAP settings update requested by {User}", command.UserName);

            var request = command.Request;

            var webConfigPath = WebConfigPath;
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

            // The Settings page never pre-fills the password and says "leave blank to keep the current
            // password", so a blank one means keep it. Writing it would blank SAP__Password and break
            // the SAP login at the next app pool restart.
            var passwordSupplied = !string.IsNullOrWhiteSpace(request.Password);
            if (passwordSupplied)
                SetEnvironmentVariable(envVarsNode, xml, "SAP__Password", request.Password!);

            if (request.InvoiceSeries.HasValue)
            {
                if (request.InvoiceSeries.Value <= 0)
                    return Errors.SAPSettings.UpdateFailed("SAP invoice series must be greater than zero.");

                SetEnvironmentVariable(envVarsNode, xml, "SAP__InvoiceSeries", request.InvoiceSeries.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (request.InvoiceSeriesName is not null)
            {
                var invoiceSeriesName = request.InvoiceSeriesName.Trim();
                if (!string.IsNullOrWhiteSpace(invoiceSeriesName))
                {
                    SetEnvironmentVariable(envVarsNode, xml, "SAP__InvoiceSeriesName", invoiceSeriesName);
                }
            }

            xml.Save(webConfigPath);

            logger.LogInformation("SAP settings updated in web.config by {User}", command.UserName);

            try { await auditService.LogAsync(AuditActions.UpdateSAPSettings, "SAPSettings", null, $"SAP settings updated by {command.UserName}", true); } catch { }

            if (request.TestConnection)
            {
                try
                {
                    // The running process still holds the configured password; web.config edits only
                    // reach IOptionsMonitor after a restart, which is the value we just kept.
                    var testPassword = passwordSupplied ? request.Password! : sapSettings.CurrentValue.Password;
                    var connected = await sapClient.TestConnectionWithCredentialsAsync(
                        request.ServiceLayerUrl, request.CompanyDB, request.UserName, testPassword,
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
