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

        var salesOrderRequest = VanSalesCompatibilityMapper.MapSalesOrderRequest(
            command.Request,
            customer,
            warehouseCode,
            costCentreCode,
            command.DeviceInfo);

        // The last point at which the caller going away stops the order: only the user and its scope have
        // been read, and nothing has been asked of SAP or stored, so there is nothing to finish.
        cancellationToken.ThrowIfCancellationRequested();

        return await CreateAsync(salesOrderRequest, command.UserId);
    }

    /// <summary>
    /// The order, from its first SAP read to its answer. Once started it runs to the end.
    /// </summary>
    /// <remarks>
    /// <para><b>It takes no token, and that is the guard.</b> ASP.NET binds the request's token to
    /// <c>HttpContext.RequestAborted</c>, and a van handset hangs up after 30 seconds. Before the order is
    /// stored, <c>SalesOrderService.CreateAsync</c> asks SAP for the sales unit of every line, and on
    /// 2026-10-07 van requests waiting on SAP outlived the handset. Cancelled there, the order was not
    /// stored, and <see cref="CreateSalesOrderHandler"/> — which answers every exception as a failed order —
    /// told nobody "The operation was canceled". The resend met the same read. Run to the end instead, the
    /// order is captured once under its van order, and the handset's resend under the same reference is
    /// handed that order by <c>ClientRequestId</c>.</para>
    ///
    /// <para>Nothing waits forever for want of a token. Every SAP read is bounded by the client's own
    /// budget; the request's token only ever added a deadline the work did not need and the caller had
    /// already stopped waiting for.</para>
    /// </remarks>
    private async Task<ErrorOr<VanSalesLegacyOrderDto>> CreateAsync(CreateSalesOrderRequest salesOrderRequest, Guid userId)
    {
        // Deliberately and literally CancellationToken.None — see the remarks. Held in a local so that a
        // future edit adding a call here cannot quietly reintroduce the request token.
        var unstoppable = CancellationToken.None;

        var result = await mediator.Send(new CreateSalesOrderCommand(salesOrderRequest, userId), unstoppable);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesCompatibilityMapper.MapLegacySalesOrder(result.Value);
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