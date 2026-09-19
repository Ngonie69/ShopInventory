using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetIncomingPaymentGlMappings;

public sealed class GetIncomingPaymentGlMappingsHandler(ApplicationDbContext db)
    : IRequestHandler<GetIncomingPaymentGlMappingsQuery, ErrorOr<List<IncomingPaymentGlMappingDto>>>
{
    public async Task<ErrorOr<List<IncomingPaymentGlMappingDto>>> Handle(
        GetIncomingPaymentGlMappingsQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.IncomingPaymentGlMappings
            .AsNoTracking()
            .OrderBy(mapping => mapping.Run)
            .ThenBy(mapping => mapping.CardCode)
            .ToListAsync(cancellationToken);

        return rows.Select(IncomingPaymentGlMappingProjection.ToDto).ToList();
    }
}
