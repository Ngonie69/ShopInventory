using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.UpdateFiscalisationSettings;

/// <summary>
/// Stores a new Fiscalisation platform API key in web.config's environment variables, the same way the
/// SAP and mobile version policy settings are stored.
/// </summary>
/// <remarks>
/// The key is a secret and deliberately has no home in appsettings.json, which is committed. web.config
/// is the deployment's own file, and the API already binds <c>Fiscalisation__ApiKey</c> from it.
/// </remarks>
public sealed class UpdateFiscalisationSettingsHandler(
    IFiscalisationApiClient fiscalisationClient,
    IOptionsMonitor<FiscalisationSettings> fiscalisationSettings,
    IAuditService auditService,
    ILogger<UpdateFiscalisationSettingsHandler> logger
) : IRequestHandler<UpdateFiscalisationSettingsCommand, ErrorOr<UpdateFiscalisationSettingsResult>>
{
    private const string ApiKeyVariableName = "Fiscalisation__ApiKey";

    public async Task<ErrorOr<UpdateFiscalisationSettingsResult>> Handle(
        UpdateFiscalisationSettingsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = command.Request.ApiKey?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(apiKey))
            {
                return Errors.FiscalisationSettings.ApiKeyRequired;
            }

            // A key that wrapped on the way through a chat window or a spreadsheet still looks fine in
            // the box. Left alone it reaches HttpClient.DefaultRequestHeaders.Add at startup, which
            // throws on an invalid header value and takes the whole fiscalisation client down with it —
            // long after whoever pasted it has gone.
            if (apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
            {
                return Errors.FiscalisationSettings.UpdateFailed(
                    "The API key contains spaces or line breaks. Paste it as a single unbroken string.");
            }

            logger.LogWarning("Fiscalisation API key update requested by {User}", command.UserName);

            bool? connectionTestPassed = null;
            var verificationNote = string.Empty;

            if (command.Request.TestConnection)
            {
                var probe = await FiscalisationApiKeyProbe.RunAsync(
                    fiscalisationClient,
                    apiKey,
                    fiscalisationSettings.CurrentValue.DefaultDeviceId,
                    logger,
                    cancellationToken);

                // Only an outright refusal stops the save. An unreachable platform proves nothing about
                // the key, and refusing to store one then would lock an administrator out of fixing
                // fiscalisation during precisely the outage they are responding to.
                if (probe.IsRejected)
                {
                    logger.LogWarning(
                        "Fiscalisation API key from {User} was not stored: the platform refused it",
                        command.UserName);

                    return Errors.FiscalisationSettings.ApiKeyRejected(
                        probe.Message + " The key was not saved.");
                }

                // Null, not false, when the probe could not decide: false reads as "the platform said no",
                // and the screen paints an unverified save amber rather than green off this.
                connectionTestPassed = probe.IsAccepted ? true : null;
                verificationNote = " " + probe.Message;
            }

            var webConfigPath = Path.Combine(AppContext.BaseDirectory, "web.config");
            if (!File.Exists(webConfigPath))
                return Errors.FiscalisationSettings.UpdateFailed(
                    "web.config not found. The API key can only be updated on IIS deployments — "
                    + "elsewhere, set the Fiscalisation__ApiKey environment variable or a user secret.");

            var xml = new System.Xml.XmlDocument();
            xml.Load(webConfigPath);

            var envVarsNode = xml.SelectSingleNode("//aspNetCore/environmentVariables");
            if (envVarsNode == null)
                return Errors.FiscalisationSettings.UpdateFailed(
                    "Could not find environmentVariables section in web.config.");

            SetEnvironmentVariable(envVarsNode, xml, ApiKeyVariableName, apiKey);

            xml.Save(webConfigPath);

            logger.LogInformation("Fiscalisation API key updated in web.config by {User}", command.UserName);

            var masked = FiscalisationApiKeyMask.Mask(apiKey);

            try
            {
                // The mask, never the key: the audit trail is read by more people than may hold it.
                await auditService.LogAsync(
                    AuditActions.UpdateFiscalisationSettings,
                    "FiscalisationSettings",
                    null,
                    $"Fiscalisation API key updated by {command.UserName} (now {masked})",
                    true);
            }
            catch
            {
            }

            return new UpdateFiscalisationSettingsResult(
                $"Fiscalisation API key saved.{verificationNote} IIS recycles the app pool when web.config "
                + "changes, so the new key normally goes live within a few seconds — the key shown here "
                + "updates once it has.",
                connectionTestPassed,
                masked);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update the Fiscalisation API key");
            return Errors.FiscalisationSettings.UpdateFailed(ex.Message);
        }
    }

    private static void SetEnvironmentVariable(
        System.Xml.XmlNode envVarsNode,
        System.Xml.XmlDocument xml,
        string name,
        string value)
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
