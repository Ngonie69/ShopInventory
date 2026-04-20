using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Commands.SubmitMobileOrder;

public sealed class SubmitMobileOrderHandler(
    ApplicationDbContext context,
    ISalesOrderService salesOrderService,
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
            return Errors.Merchandiser.AssignmentFailed($"The following items are not assigned to you: {string.Join(", ", unassigned)}");

        var salesOrderRequest = new CreateSalesOrderRequest
        {
            CardCode = command.Request.CardCode,
            CardName = command.Request.CardName,
            DeliveryDate = command.Request.DeliveryDate,
            Comments = command.Request.Notes,
            Source = SalesOrderSource.Mobile,
            MerchandiserNotes = command.Request.Notes,
            DeviceInfo = command.Request.DeviceInfo,
            Lines = command.Request.Items.Select(item => new CreateSalesOrderLineRequest
            {
                ItemCode = item.ItemCode,
                ItemDescription = item.ItemName,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice
            }).ToList()
        };

        try
        {
            var order = await salesOrderService.CreateAsync(salesOrderRequest, command.UserId, cancellationToken);
            logger.LogInformation("Merchandiser {UserId} submitted mobile order {OrderNumber} for customer {CardCode} with {LineCount} items",
                command.UserId, order.OrderNumber, command.Request.CardCode, command.Request.Items.Count);
            return order;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating merchandiser mobile order for customer {CardCode}", command.Request.CardCode);
            return Errors.Merchandiser.AssignmentFailed(ex.InnerException?.Message ?? ex.Message);
        }
    }
}
