using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerDocumentDeliverySettings;

/// <summary>
/// Saves the run-time switches to <c>SystemConfigs</c>, where every node's next pass reads them.
/// </summary>
/// <remarks>
/// A session is accepted only if the gateway knows it, when this node can ask: a mistyped id would
/// otherwise leave every document waiting on a session that does not exist. Saving with no session
/// hands the choice back to the system (<see cref="CustomerDocumentSession"/>); stopping all sending
/// is asked for by name, and recorded, so a session nobody chose is not picked over it.
/// </remarks>
public sealed class UpdateCustomerDocumentDeliverySettingsHandler(
    ApplicationDbContext context,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaOptions,
    IOptions<CustomerDocumentDeliverySettings> options,
    IAuditService auditService,
    ILogger<UpdateCustomerDocumentDeliverySettingsHandler> logger)
    : IRequestHandler<UpdateCustomerDocumentDeliverySettingsCommand, ErrorOr<CustomerDocumentDeliveryStatusDto>>
{
    public async Task<ErrorOr<CustomerDocumentDeliveryStatusDto>> Handle(
        UpdateCustomerDocumentDeliverySettingsCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var sessionId = string.IsNullOrWhiteSpace(request.WhatsAppSessionId) ? null : request.WhatsAppSessionId.Trim();
        var stopped = sessionId is null && request.StopSending;

        if (sessionId is not null && WhatsAppGateway.IsConfigured(openWaOptions.Value))
        {
            try
            {
                var sessions = await openWaClient.GetSessionsAsync(cancellationToken);
                if (!sessions.Any(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Errors.CustomerDocuments.SettingsInvalid(
                        "The gateway has no session with that id. Pick one from the list.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not confirm WhatsApp session {SessionId} before saving it", sessionId);
                return Errors.CustomerDocuments.GatewayUnavailable(
                    "The WhatsApp gateway could not be asked whether that session exists. Try again in a moment.");
            }
        }

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.AutoSendEnabled, "bool",
            request.AutoSendEnabled ? "true" : "false",
            "Whether invoices are sent to opted-in customers without anyone pressing Send.",
            isEditable: true, cancellationToken);

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.WhatsAppSessionId, "string",
            sessionId,
            "The OpenWA session customer documents are sent from. Blank leaves it to be chosen automatically.",
            isEditable: true, cancellationToken);

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.SendingStopped, "bool",
            stopped ? "true" : "false",
            "Whether an administrator stopped all sending. While true no session is used and none is chosen automatically.",
            isEditable: false, cancellationToken);

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.MaxAutoPerDay, "int",
            request.MaxAutoPerDay.ToString(CultureInfo.InvariantCulture),
            "The most automatic document sends in a CAT day.",
            isEditable: true, cancellationToken);

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.SettingsChangedBy, "string",
            actorName,
            "Who last changed the customer document settings.",
            isEditable: false, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateCustomerDocumentSettings,
                "CustomerDocumentSettings",
                null,
                $"Automatic sending {(request.AutoSendEnabled ? "on" : "off")}; session {sessionId ?? (stopped ? "none, all sending stopped" : "chosen automatically")}; "
                + $"automatic cap {request.MaxAutoPerDay} a day",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the customer document settings change");
        }

        return await CustomerDocumentStatusReader.ReadAsync(
            context, openWaClient, openWaOptions.Value, options.Value, logger, cancellationToken);
    }
}
