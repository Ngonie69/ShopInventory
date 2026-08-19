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

        // This path shares VanSalesOrderRequest with the direct-invoice endpoint, so it can be handed a
        // signed receipt — and it has nowhere to put one. It converts an existing sales order and
        // fiscalises the resulting invoice server-side, which for a handset that stamped is a second
        // writer on a chain that must have exactly one: FDMS then refuses that device's whole fiscal day
        // at upload, not just this receipt.
        //
        // Refused rather than quietly dropped or quietly forked. Both of those are found days later, in
        // the fiscal day that will not close; this costs one conversion and says why.
        if (command.Request.ClaimsReceiptSequence())
        {
            return Error.Validation(
                "VanSalesCompatibility.StampedSaleCannotBeConverted",
                "This request carries a fiscal receipt the handset signed, and sales order conversion " +
                "cannot take custody of one. Send a stamped sale to the direct invoice endpoint instead.");
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