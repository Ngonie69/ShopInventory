using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetMerchandisers;

public sealed class GetMerchandisersHandler(
    ApplicationDbContext context
) : IRequestHandler<GetMerchandisersQuery, ErrorOr<List<MerchandiserSummaryDto>>>
{
    public async Task<ErrorOr<List<MerchandiserSummaryDto>>> Handle(
        GetMerchandisersQuery request,
        CancellationToken cancellationToken)
    {
        var merchandisers = await context.Users
            .AsNoTracking()
            .Where(u => u.Role == "Merchandiser" && u.IsActive)
            .Select(u => new MerchandiserSummaryDto
            {
                UserId = u.Id,
                Username = u.Username,
                FirstName = u.FirstName,
                LastName = u.LastName,
                AssignedCustomers = string.IsNullOrEmpty(u.AssignedCustomerCodes) ? 0 :
                    u.AssignedCustomerCodes.Length - u.AssignedCustomerCodes.Replace(",", "").Length,
                TotalProducts = context.MerchandiserProducts.Count(mp => mp.MerchandiserUserId == u.Id),
                ActiveProducts = context.MerchandiserProducts.Count(mp => mp.MerchandiserUserId == u.Id && mp.IsActive)
            })
            .ToListAsync(cancellationToken);

        foreach (var m in merchandisers)
        {
            var user = await context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == m.UserId, cancellationToken);
            if (user != null)
            {
                m.AssignedCustomers = user.GetCustomerCodes().Count;
            }
        }

        return merchandisers;
    }
}
