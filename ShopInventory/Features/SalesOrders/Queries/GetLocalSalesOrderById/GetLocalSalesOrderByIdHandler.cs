using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.GetLocalSalesOrderById;

public sealed class GetLocalSalesOrderByIdHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ISalesOrderService salesOrderService,
    ILogger<GetLocalSalesOrderByIdHandler> logger
) : IRequestHandler<GetLocalSalesOrderByIdQuery, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        GetLocalSalesOrderByIdQuery request,
        CancellationToken cancellationToken)
    {
        await RepairLocalMobileOrderIfNeededAsync(request.Id, cancellationToken);

        var order = await salesOrderService.GetByIdFromLocalAsync(request.Id, cancellationToken);
        if (order is null)
            return Errors.SalesOrder.NotFound(request.Id);

        return order;
    }

    private async Task RepairLocalMobileOrderIfNeededAsync(int id, CancellationToken cancellationToken)
    {
        var order = await context.SalesOrders
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null ||
            order.Source != SalesOrderSource.Mobile ||
            order.Status != SalesOrderStatus.Approved ||
            (order.IsSynced && order.SAPDocNum.HasValue && order.SAPDocNum > 0) ||
            string.IsNullOrWhiteSpace(order.OrderNumber))
        {
            return;
        }

        var sapOrder = await sapClient.GetSalesOrderByOrderNumberAsync(order.OrderNumber, cancellationToken);
        if (sapOrder == null || sapOrder.DocNum <= 0)
            return;

        order.SAPDocEntry = sapOrder.DocEntry;
        order.SAPDocNum = sapOrder.DocNum;
        order.IsSynced = true;
        order.SyncError = null;
        order.Status = SalesOrderStatus.Approved;
        order.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Repaired mobile sales order {OrderId} ({OrderNumber}) with SAP DocEntry={DocEntry}, DocNum={DocNum} during local detail retrieval.",
            order.Id,
            order.OrderNumber,
            sapOrder.DocEntry,
            sapOrder.DocNum);
    }
}
