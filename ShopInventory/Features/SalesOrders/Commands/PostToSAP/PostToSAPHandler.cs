using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.PostToSAP;

public sealed class PostToSAPHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    ILogger<PostToSAPHandler> logger
) : IRequestHandler<PostToSAPCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        PostToSAPCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.PostToSAPAsync(command.Id, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.PostSalesOrderToSAP, "SalesOrder", command.Id.ToString(), $"Sales order {command.Id} posted to SAP", true); } catch { }
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error posting sales order {Id} to SAP", command.Id);
            return Errors.SalesOrder.SapError(ex.Message);
        }
    }
}
