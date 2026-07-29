using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesSalesOrder;

public sealed class CreateVanSalesSalesOrderHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ILogger<CreateVanSalesSalesOrderHandler> logger
) : IRequestHandler<CreateVanSalesSalesOrderCommand, ErrorOr<VanSalesLegacyOrderDto>>
{
    public async Task<ErrorOr<VanSalesLegacyOrderDto>> Handle(
        CreateVanSalesSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.Request.Type) &&
            !string.Equals(command.Request.Type, "SO", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidOrderType",
                "Only sales-order payloads are supported by the van sales sales-order endpoint.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required for van sales sales orders.");
        }

        var costCentreCode = VanSalesCompatibilityMapper.ResolveAssignedCostCentreCode(user);
        if (string.IsNullOrWhiteSpace(costCentreCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingCostCentre",
                "An assigned cost centre is required for van sales sales orders.");
        }

        var cardCode = await ResolvePostingCustomerCodeAsync(
            command.Request,
            user,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(cardCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidCustomer",
                "The selected customer is not assigned to the current user.");
        }

        var salesOrderRequest = VanSalesCompatibilityMapper.MapSalesOrderRequest(
            command.Request,
            cardCode,
            warehouseCode,
            costCentreCode);

        var result = await mediator.Send(
            new CreateSalesOrderCommand(salesOrderRequest, command.UserId),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesCompatibilityMapper.MapLegacySalesOrder(result.Value);
    }

    private async Task<string?> ResolvePostingCustomerCodeAsync(
        VanSalesOrderRequest request,
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var routeCustomers = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(db, user, cancellationToken);
            var selectedCustomer = routeCustomers.FirstOrDefault(customer => MatchesRequestedCustomer(request, customer.Code));
            return selectedCustomer is null
                ? null
                : user.AssignedBusinessPartnerCode?.Trim();
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var normalizedCodes = effectiveCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(request.CustomerCode))
        {
            var requestedCode = request.CustomerCode.Trim();
            return normalizedCodes.Contains(requestedCode, StringComparer.OrdinalIgnoreCase)
                ? requestedCode
                : null;
        }

        return normalizedCodes.FirstOrDefault(code => VanSalesCompatibilityMapper.EncodeCompatibilityId(code) == request.Customer);
    }

    private static bool MatchesRequestedCustomer(VanSalesOrderRequest request, string code)
    {
        if (!string.IsNullOrWhiteSpace(request.CustomerCode) &&
            string.Equals(request.CustomerCode.Trim(), code, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return VanSalesCompatibilityMapper.EncodeCompatibilityId(code) == request.Customer;
    }
}