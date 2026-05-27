using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;

public sealed class GetVanSalesCustomersHandler(
    ApplicationDbContext db,
    IBusinessPartnerService businessPartnerService,
    ILogger<GetVanSalesCustomersHandler> logger
) : IRequestHandler<GetVanSalesCustomersQuery, ErrorOr<List<VanSalesShopDto>>>
{
    public async Task<ErrorOr<List<VanSalesShopDto>>> Handle(
        GetVanSalesCustomersQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == query.UserId, cancellationToken);

        if (user is null)
        {
            return Error.NotFound("VanSalesCompatibility.UserNotFound", "User was not found.");
        }

        if (!user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.UserInactive", "User is not active.");
        }

        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var routeCustomers = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
                db,
                user,
                cancellationToken);
            var assignedBusinessPartnerCode = user.AssignedBusinessPartnerCode?.Trim();
            var partner = !string.IsNullOrWhiteSpace(assignedBusinessPartnerCode)
                ? await businessPartnerService.GetBusinessPartnerByCodeAsync(assignedBusinessPartnerCode, cancellationToken)
                : null;

            return routeCustomers
                .Select(routeCustomer => VanSalesCompatibilityMapper.MapShop(user, routeCustomer, partner))
                .ToList();
        }

        var customerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var normalizedCodes = customerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shops = new List<VanSalesShopDto>(normalizedCodes.Count);
        foreach (var customerCode in normalizedCodes)
        {
            var partner = await businessPartnerService.GetBusinessPartnerByCodeAsync(customerCode, cancellationToken);
            shops.Add(VanSalesCompatibilityMapper.MapShop(user, customerCode, partner));
        }

        return shops;
    }
}