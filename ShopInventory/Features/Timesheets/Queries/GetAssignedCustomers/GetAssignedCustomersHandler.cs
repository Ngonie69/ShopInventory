using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Timesheets.Queries.GetAssignedCustomers;

public sealed class GetAssignedCustomersHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<GetAssignedCustomersHandler> logger
) : IRequestHandler<GetAssignedCustomersQuery, ErrorOr<List<AssignedCustomerDto>>>
{
    public async Task<ErrorOr<List<AssignedCustomerDto>>> Handle(
        GetAssignedCustomersQuery request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user is null)
            return Common.Errors.Errors.Auth.UserNotFound;

        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var routeCustomers = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
                db,
                user,
                cancellationToken);

            var activeRouteCustomerCode = await db.TimesheetEntries
                .AsNoTracking()
                .Where(t => t.UserId == request.UserId && t.CheckOutTime == null)
                .OrderByDescending(t => t.CheckInTime)
                .ThenByDescending(t => t.Id)
                .Select(t => t.CustomerCode)
                .FirstOrDefaultAsync(cancellationToken);

            return routeCustomers
                .Select(customer => new AssignedCustomerDto(
                    customer.Code,
                    customer.Name,
                    HasActiveCheckIn: customer.Code == activeRouteCustomerCode))
                .ToList();
        }

        var customerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        if (customerCodes.Count == 0)
        {
            try
            {
                await auditService.LogAsync(
                    AuditActions.ViewAssignedCustomers,
                    "User",
                    request.UserId.ToString(),
                    "Loaded 0 assigned customers.",
                    true);
            }
            catch
            {
            }

            return new List<AssignedCustomerDto>();
        }

        // Check if user has an active check-in
        var activeCheckInCustomerCode = await db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.UserId == request.UserId && t.CheckOutTime == null)
            .OrderByDescending(t => t.CheckInTime)
            .ThenByDescending(t => t.Id)
            .Select(t => t.CustomerCode)
            .FirstOrDefaultAsync(cancellationToken);

        // The price-catalog sync already maintains business-partner names locally. Resolve all
        // assigned names in one database query instead of issuing one SAP request per customer;
        // the previous fan-out saturated the shared SAP concurrency gate during mobile polling.
        var cachedProfiles = await db.BusinessPartnerPriceProfiles
            .AsNoTracking()
            .Where(profile => customerCodes.Contains(profile.CardCode))
            .Select(profile => new { profile.CardCode, profile.CardName })
            .ToListAsync(cancellationToken);

        var cachedNames = cachedProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.CardName))
            .ToDictionary(
                profile => profile.CardCode,
                profile => profile.CardName!,
                StringComparer.OrdinalIgnoreCase);

        var results = customerCodes
            .Select(code => new AssignedCustomerDto(
                code,
                cachedNames.GetValueOrDefault(code, code),
                HasActiveCheckIn: code == activeCheckInCustomerCode))
            .ToList();

        try
        {
            await auditService.LogAsync(
                AuditActions.ViewAssignedCustomers,
                "User",
                request.UserId.ToString(),
                $"Loaded {results.Count} assigned customer(s).",
                true);
        }
        catch
        {
        }

        return results;
    }
}
