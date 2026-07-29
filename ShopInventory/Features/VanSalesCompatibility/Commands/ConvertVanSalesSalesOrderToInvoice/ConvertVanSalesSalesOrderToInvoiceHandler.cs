using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;

public sealed class ConvertVanSalesSalesOrderToInvoiceHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<ConvertVanSalesSalesOrderToInvoiceCommand, ErrorOr<VanSalesConvertSalesOrderToInvoiceResponse>>
{
    public async Task<ErrorOr<VanSalesConvertSalesOrderToInvoiceResponse>> Handle(
        ConvertVanSalesSalesOrderToInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var salesOrderId = VanSalesCompatibilityMapper.ParseSalesOrderId(command.Request);
        if (salesOrderId is null)
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidSalesOrderId",
                "A valid sales order identifier is required for invoice conversion.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required for sales order conversion.");
        }

        var costCentreCode = VanSalesCompatibilityMapper.ResolveAssignedCostCentreCode(user);
        if (string.IsNullOrWhiteSpace(costCentreCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingCostCentre",
                "An assigned cost centre is required for sales order conversion.");
        }

        var convertRequest = VanSalesCompatibilityMapper.MapConvertRequest(
            command.Request,
            salesOrderId.Value,
            warehouseCode,
            costCentreCode);

        var result = await mediator.Send(
            new ConvertSalesOrderToInvoiceCommand(convertRequest, command.UserId.ToString()),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesCompatibilityMapper.MapConvertResponse(result.Value);
    }
}