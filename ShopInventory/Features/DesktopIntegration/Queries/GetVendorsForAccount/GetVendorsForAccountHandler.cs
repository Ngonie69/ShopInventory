using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetVendorsForAccount;

public sealed class GetVendorsForAccountHandler(
    ApplicationDbContext context
) : IRequestHandler<GetVendorsForAccountQuery, ErrorOr<List<DesktopVendorDto>>>
{
    public async Task<ErrorOr<List<DesktopVendorDto>>> Handle(
        GetVendorsForAccountQuery query,
        CancellationToken cancellationToken)
    {
        // The shop is included for the same reason the sale includes it: a till operator's business
        // partner lives on the shop rather than on the account, and the resolver refuses to fall back
        // to the account's own columns when a shop is named but not loaded.
        var user = await context.Users
            .AsNoTracking()
            .Include(candidate => candidate.Shop)
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        var assignments = SellingAccountResolver.Resolve(user);
        if (assignments.IsError)
        {
            return assignments.Errors;
        }

        var cardCode = assignments.Value.CardCode;

        // Active only, and filtered exactly as the sale filters: this list is a promise that every
        // row on it can be sold to, and a removed vendor that stayed on it would be a code the
        // operator can pick and the server will then refuse.
        return await context.RouteCustomers
            .AsNoTracking()
            .Where(vendor => vendor.AssignedBusinessPartnerCode == cardCode && vendor.IsActive)
            .OrderBy(vendor => vendor.Surname ?? vendor.Name)
            .ThenBy(vendor => vendor.Name)
            .ThenBy(vendor => vendor.Code)
            .Select(vendor => new DesktopVendorDto
            {
                Code = vendor.Code,
                Name = vendor.Name,
                Surname = vendor.Surname,
                Phone = vendor.Phone,
                VatNumber = vendor.VatNumber
            })
            .ToListAsync(cancellationToken);
    }
}
