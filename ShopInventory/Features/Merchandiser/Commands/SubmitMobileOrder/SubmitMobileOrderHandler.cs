using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Commands.SubmitMobileOrder;

public sealed class SubmitMobileOrderHandler(
    ApplicationDbContext context,
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    ILogger<SubmitMobileOrderHandler> logger
) : IRequestHandler<SubmitMobileOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        SubmitMobileOrderCommand command,
        CancellationToken cancellationToken)
    {
        // All merchandisers share the same active product list (no per-user filtering)
        var assignedItemCodes = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.IsActive)
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        var requestedItemCodes = command.Request.Items.Select(i => i.ItemCode).ToList();
        var unassigned = requestedItemCodes.Except(assignedItemCodes, StringComparer.OrdinalIgnoreCase).ToList();
        if (unassigned.Count > 0)
        {
            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateMobileSalesOrder,
                    "SalesOrder",
                    null,
                    $"Rejected mobile order for {command.Request.CardCode}: unassigned items {string.Join(", ", unassigned)}",
                    false,
                    "One or more items are not assigned to the merchandiser.");
            }
            catch
            {
            }

            return Errors.Merchandiser.AssignmentFailed($"The following items are not assigned to you: {string.Join(", ", unassigned)}");
        }

        var salesOrderRequest = new CreateSalesOrderRequest
        {
            CardCode = command.Request.CardCode,
            CardName = command.Request.CardName,
            DeliveryDate = command.Request.DeliveryDate,
            Comments = command.Request.Notes,
            Currency = command.Request.Currency?.Trim().ToUpperInvariant(),
            Source = SalesOrderSource.Mobile,
            ClientRequestId = string.IsNullOrWhiteSpace(command.Request.ClientRequestId)
                ? null
                : command.Request.ClientRequestId.Trim(),
            MerchandiserNotes = command.Request.Notes,
            DeviceInfo = command.Request.DeviceInfo,
            Lines = command.Request.Items.Select(item => new CreateSalesOrderLineRequest
            {
                ItemCode = item.ItemCode,
                ItemDescription = item.ItemName,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                UoMCode = item.UoMCode
            }).ToList()
        };

        try
        {
            var order = await salesOrderService.CreateAsync(salesOrderRequest, command.UserId, cancellationToken);
            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateMobileSalesOrder,
                    "SalesOrder",
                    order.Id.ToString(),
                    $"Mobile order {order.OrderNumber} submitted for {command.Request.CardCode} with {command.Request.Items.Count} items.",
                    true);
            }
            catch
            {
            }

            logger.LogInformation("Merchandiser {UserId} submitted mobile order {OrderNumber} for customer {CardCode} with {LineCount} items",
                command.UserId, order.OrderNumber, command.Request.CardCode, command.Request.Items.Count);
            return order;
        }
        catch (CreditLimitExceededException ex)
        {
            // A safety net rather than the normal path: capture no longer refuses a mobile order on
            // credit, because an unpriced order can only be measured against the customer's
            // standing balance and losing the capture helps nobody. The order is held on the web
            // instead and refused at the point of posting. If something upstream does refuse one
            // here, report it as a validation failure rather than through AssignmentFailed — a 500
            // has its detail replaced with a generic message, and the rep in the field would be
            // told the order failed without being told the account is over its limit.
            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateMobileSalesOrder,
                    "SalesOrder",
                    null,
                    $"Refused mobile order for {command.Request.CardCode} on credit.",
                    false,
                    ex.Message);
            }
            catch
            {
            }

            logger.LogWarning(
                "Refused a merchandiser mobile order for {CardCode} on credit: {Reason}",
                command.Request.CardCode,
                ex.Message);
            return Errors.SalesOrder.CreditLimitExceeded(ex.Message);
        }
        catch (Exception ex)
        {
            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateMobileSalesOrder,
                    "SalesOrder",
                    null,
                    $"Failed mobile order submission for {command.Request.CardCode} with {command.Request.Items.Count} items.",
                    false,
                    ex.InnerException?.Message ?? ex.Message);
            }
            catch
            {
            }

            logger.LogError(ex, "Error creating merchandiser mobile order for customer {CardCode}", command.Request.CardCode);
            return Errors.Merchandiser.AssignmentFailed(ex.InnerException?.Message ?? ex.Message);
        }
    }
}
