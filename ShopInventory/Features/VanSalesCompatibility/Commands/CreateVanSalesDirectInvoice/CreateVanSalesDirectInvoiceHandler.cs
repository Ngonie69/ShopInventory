using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesDirectInvoice;

public sealed class CreateVanSalesDirectInvoiceHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ILogger<CreateVanSalesDirectInvoiceHandler> logger
) : IRequestHandler<CreateVanSalesDirectInvoiceCommand, ErrorOr<VanSalesDirectInvoiceResponse>>
{
    public async Task<ErrorOr<VanSalesDirectInvoiceResponse>> Handle(
        CreateVanSalesDirectInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.Request.Type) &&
            !string.Equals(command.Request.Type, "INV", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidOrderType",
                "Only invoice payloads are supported by the direct van sales invoice endpoint.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required for van sales invoicing.");
        }

        var costCentreCode = VanSalesCompatibilityMapper.ResolveAssignedCostCentreCode(user);
        if (string.IsNullOrWhiteSpace(costCentreCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingCostCentre",
                "An assigned cost centre is required for van sales invoicing.");
        }

        var customer = await ResolveCustomerAsync(
            command.Request,
            user,
            cancellationToken);
        if (customer is null)
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidCustomer",
                "The selected customer is not assigned to the current user.");
        }

        var invoiceRequest = VanSalesCompatibilityMapper.MapInvoiceRequest(
            command.Request,
            customer,
            warehouseCode,
            costCentreCode);

        var result = await mediator.Send(
            new CreateInvoiceDirectCommand(invoiceRequest, command.UserId.ToString()),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesCompatibilityMapper.MapInvoiceResponse(result.Value, command.Request.VanOrder);
    }

    private async Task<VanSalesCustomerResolution?> ResolveCustomerAsync(
        VanSalesOrderRequest request,
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var routeCustomers = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(db, user, cancellationToken);
            var selectedCustomer = routeCustomers.FirstOrDefault(
                customer => VanSalesCompatibilityMapper.MatchesRequestedCustomer(request, customer.Code));

            var postingCardCode = user.AssignedBusinessPartnerCode?.Trim();
            return selectedCustomer is null || string.IsNullOrWhiteSpace(postingCardCode)
                ? null
                : new VanSalesCustomerResolution(postingCardCode, selectedCustomer);
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
                ? new VanSalesCustomerResolution(requestedCode, null)
                : null;
        }

        var encodedMatch = normalizedCodes.FirstOrDefault(
            code => VanSalesCompatibilityMapper.EncodeCompatibilityId(code) == request.Customer);

        return encodedMatch is null ? null : new VanSalesCustomerResolution(encodedMatch, null);
    }
}