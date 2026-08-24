using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RouteCustomers.Commands.SetRouteCustomerVisitDays;

/// <summary>
/// Replaces a shop's calling days with the set supplied.
/// </summary>
/// <remarks>
/// The existing rows are deleted and the new ones inserted in one save, so a customer is never
/// briefly left with no schedule. Duplicates in the request are collapsed rather than rejected: the
/// same day sent twice is a client repeating itself, not a different instruction, and the unique
/// index would otherwise turn it into an error the operator cannot act on.
/// </remarks>
public sealed class SetRouteCustomerVisitDaysHandler(
    ApplicationDbContext context,
    ILogger<SetRouteCustomerVisitDaysHandler> logger)
    : IRequestHandler<SetRouteCustomerVisitDaysCommand, ErrorOr<RouteCustomerVisitDaysResult>>
{
    public async Task<ErrorOr<RouteCustomerVisitDaysResult>> Handle(
        SetRouteCustomerVisitDaysCommand command,
        CancellationToken cancellationToken)
    {
        var routeCustomer = await context.RouteCustomers
            .AsNoTracking()
            .Where(c => c.Id == command.RouteCustomerId)
            .Select(c => new { c.Id, c.Code, c.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (routeCustomer is null)
        {
            return Errors.RouteCustomers.NotFound(command.RouteCustomerId);
        }

        var days = command.VisitDays
            .Distinct()
            .OrderBy(day => day)
            .ToList();

        await context.RouteCustomerVisitDays
            .Where(d => d.RouteCustomerId == routeCustomer.Id)
            .ExecuteDeleteAsync(cancellationToken);

        if (days.Count > 0)
        {
            context.RouteCustomerVisitDays.AddRange(days.Select(day => new RouteCustomerVisitDayEntity
            {
                RouteCustomerId = routeCustomer.Id,
                DayOfWeek = day,
                CreatedAt = DateTime.UtcNow
            }));

            await context.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Set calling days for route customer {Code} to {Days}.",
            routeCustomer.Code,
            days.Count == 0 ? "(none)" : string.Join(", ", days));

        return new RouteCustomerVisitDaysResult(
            routeCustomer.Id,
            routeCustomer.Code,
            routeCustomer.Name,
            days);
    }
}
