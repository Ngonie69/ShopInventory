using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.CancelCustomerDocumentDelivery;

/// <summary>
/// Withdraws a waiting delivery, in one conditional update: if the job claimed it a moment earlier,
/// the update finds nothing to change and the person is told it is already on its way.
/// </summary>
public sealed class CancelCustomerDocumentDeliveryHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<CancelCustomerDocumentDeliveryHandler> logger)
    : IRequestHandler<CancelCustomerDocumentDeliveryCommand, ErrorOr<CustomerDocumentDeliveryDto>>
{
    public async Task<ErrorOr<CustomerDocumentDeliveryDto>> Handle(
        CancelCustomerDocumentDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var now = DateTime.UtcNow;
        var cancelled = await CustomerDocumentDeliveryCancellation.CancelWaitingAsync(
            context,
            delivery => delivery.Id == command.DeliveryId,
            $"Withdrawn by {actorName} before it went.",
            actorName,
            now,
            cancellationToken);

        var delivery = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .Where(row => row.Id == command.DeliveryId)
            .Select(CustomerDocumentProjections.Delivery)
            .FirstOrDefaultAsync(cancellationToken);

        if (delivery is null)
            return Errors.CustomerDocuments.DeliveryNotFound(command.DeliveryId);

        if (cancelled == 0)
            return Errors.CustomerDocuments.DeliveryNotCancellable(delivery.Status);

        try
        {
            await auditService.LogAsync(
                AuditActions.CancelDocumentWhatsApp,
                "CustomerDocumentDelivery",
                command.DeliveryId.ToString(CultureInfo.InvariantCulture),
                $"Withdrew document {delivery.DocumentNumber} before it was sent to {delivery.RecipientMasked}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the withdrawal of WhatsApp delivery {DeliveryId}", command.DeliveryId);
        }

        return delivery;
    }
}
