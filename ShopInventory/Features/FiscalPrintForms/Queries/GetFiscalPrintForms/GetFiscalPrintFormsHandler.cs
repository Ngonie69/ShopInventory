using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.FiscalPrintForms.Queries.GetFiscalPrintForms;

public sealed class GetFiscalPrintFormsHandler(ApplicationDbContext db)
    : IRequestHandler<GetFiscalPrintFormsQuery, ErrorOr<List<FiscalPrintFormDto>>>
{
    public async Task<ErrorOr<List<FiscalPrintFormDto>>> Handle(
        GetFiscalPrintFormsQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.BusinessPartnerFiscalPrintForms
            .AsNoTracking()
            .OrderBy(row => row.CardCode)
            .ToListAsync(cancellationToken);

        return rows.Select(FiscalPrintFormProjection.ToDto).ToList();
    }
}
