using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscal;

public sealed class GetVanSalesFiscalHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanSalesFiscalQuery, ErrorOr<VanSalesLegacyFiscalDto>>
{
    public async Task<ErrorOr<VanSalesLegacyFiscalDto>> Handle(
        GetVanSalesFiscalQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var transaction = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(entry => entry.DocumentType == "Invoice")
            .OrderByDescending(entry => entry.TimestampUtc)
            .ThenByDescending(entry => entry.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return VanSalesCompatibilityMapper.MapLegacyFiscal(transaction);
    }
}