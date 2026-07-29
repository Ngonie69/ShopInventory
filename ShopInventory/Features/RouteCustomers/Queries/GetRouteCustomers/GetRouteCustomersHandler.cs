using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;

public sealed class GetRouteCustomersHandler(
    ApplicationDbContext context
) : IRequestHandler<GetRouteCustomersQuery, ErrorOr<List<RouteCustomerDto>>>
{
    public async Task<ErrorOr<List<RouteCustomerDto>>> Handle(
        GetRouteCustomersQuery query,
        CancellationToken cancellationToken)
    {
        var routeCustomersQuery = context.RouteCustomers
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.AssignedBusinessPartnerCode))
        {
            var assignedBusinessPartnerCode = query.AssignedBusinessPartnerCode.Trim();
            routeCustomersQuery = routeCustomersQuery
                .Where(customer => customer.AssignedBusinessPartnerCode == assignedBusinessPartnerCode);
        }

        if (query.ActiveOnly)
        {
            routeCustomersQuery = routeCustomersQuery
                .Where(customer => customer.IsActive);
        }

        return await routeCustomersQuery
            .OrderBy(customer => customer.AssignedBusinessPartnerCode)
            .ThenBy(customer => customer.Name)
            .ThenBy(customer => customer.Code)
            .Select(customer => new RouteCustomerDto
            {
                Id = customer.Id,
                AssignedBusinessPartnerCode = customer.AssignedBusinessPartnerCode,
                Code = customer.Code,
                Name = customer.Name,
                Phone = customer.Phone,
                Email = customer.Email,
                Address = customer.Address,
                VatNumber = customer.VatNumber,
                IsActive = customer.IsActive,
                CreatedByUserId = customer.CreatedByUserId,
                CreatedByUserName = customer.CreatedByUser != null ? customer.CreatedByUser.Username : null,
                CreatedAt = customer.CreatedAt,
                UpdatedAt = customer.UpdatedAt
            })
            .ToListAsync(cancellationToken);
    }
}