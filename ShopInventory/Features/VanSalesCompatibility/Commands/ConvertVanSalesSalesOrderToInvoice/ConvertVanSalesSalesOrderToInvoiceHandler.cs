using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;

public sealed class ConvertVanSalesSalesOrderToInvoiceHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ILogger<ConvertVanSalesSalesOrderToInvoiceHandler> logger
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

        // The order has to be one this account actually calls on. The id arrives in the request body
        // and nothing downstream reads the caller's scope — the inner handler fetches by id and asks
        // only whether the order exists, is approved and has lines — so without this any id converts.
        // What that costs is not abstract: the invoice bills the *order's* CardCode, which is another
        // van's business partner, while the lines come off the caller's own warehouse, and it is
        // fiscalised on the way out. Sales order ids are sequential.
        //
        // An empty scope refuses everything, which is the same direction the history read takes.
        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var orderCardCode = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Id == salesOrderId.Value)
            .Select(order => order.CardCode)
            .FirstOrDefaultAsync(cancellationToken);

        // Case-insensitively, because the codes are stored as they were typed and refusing a real
        // order over its casing is the more expensive mistake: the rep is standing at the counter.
        if (orderCardCode is null ||
            !effectiveCustomerCodes.Contains(orderCardCode, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Van sales user {UserId} asked to convert sales order {SalesOrderId}, which is outside their customer scope",
                command.UserId,
                salesOrderId.Value);

            // Said the same way whether the order belongs to another van or does not exist at all, so
            // the endpoint cannot be walked to find out which ids are real.
            return Error.Validation(
                "VanSalesCompatibility.SalesOrderNotFound",
                $"Sales order {salesOrderId.Value} was not found for this account.");
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