using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.RecheckCustomerWhatsAppContact;

/// <summary>
/// Checks a saved number against WhatsApp straight away — the one call to the gateway a person waits
/// on, and it sends nothing. It counts against the same daily cap as the checks the delivery job makes.
/// </summary>
public sealed class RecheckCustomerWhatsAppContactHandler(
    ApplicationDbContext context,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaOptions,
    IOptions<CustomerDocumentDeliverySettings> options,
    ILogger<RecheckCustomerWhatsAppContactHandler> logger)
    : IRequestHandler<RecheckCustomerWhatsAppContactCommand, ErrorOr<CustomerWhatsAppContactDto>>
{
    private static readonly Func<CustomerWhatsAppContactEntity, CustomerWhatsAppContactDto> ToDto =
        CustomerDocumentProjections.Contact.Compile();

    public async Task<ErrorOr<CustomerWhatsAppContactDto>> Handle(
        RecheckCustomerWhatsAppContactCommand command,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Errors.CustomerDocuments.Disabled;

        if (!WhatsAppGateway.IsConfigured(openWaOptions.Value))
            return Errors.CustomerDocuments.GatewayUnavailable("WhatsApp is not configured on the server that answered. Try again shortly.");

        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
        if (runtime.WhatsAppSessionId is null)
            return Errors.CustomerDocuments.SessionNotConfigured;

        var contact = await context.CustomerWhatsAppContacts
            .AsTracking()
            .FirstOrDefaultAsync(row => row.Id == command.ContactId && row.RemovedAtUtc == null, cancellationToken);

        if (contact is null)
            return Errors.CustomerDocuments.ContactNotFound(command.ContactId);

        var now = DateTime.UtcNow;
        var checksToday = await WhatsAppGateway.NumberChecksSinceAsync(context, DeliveryBudget.CatDayStartUtc(now), cancellationToken);
        if (checksToday >= options.Value.MaxNumberChecksPerDay)
        {
            return Errors.CustomerDocuments.GatewayUnavailable(
                $"Today's {options.Value.MaxNumberChecksPerDay} WhatsApp number checks have been used. The number is checked again before its next send.");
        }

        WhatsAppNumberCheckDto check;
        try
        {
            check = await openWaClient.CheckNumberAsync(
                runtime.WhatsAppSessionId,
                WhatsAppRecipients.Digits(contact.PhoneE164),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check WhatsApp contact {ContactId} against WhatsApp", contact.Id);
            return Errors.CustomerDocuments.GatewayUnavailable(
                "WhatsApp could not be asked about the number. The documents session may be disconnected.");
        }

        contact.WhatsAppExists = check.Exists;
        contact.WhatsAppCheckedAtUtc = now;
        contact.UpdatedAtUtc = now;
        contact.UpdatedBy = actorName;

        await context.SaveChangesAsync(cancellationToken);

        return ToDto(contact);
    }
}
