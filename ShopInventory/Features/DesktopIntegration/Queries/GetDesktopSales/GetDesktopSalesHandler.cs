using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;

public sealed class GetDesktopSalesHandler(ApplicationDbContext db)
    : IRequestHandler<GetDesktopSalesQuery, ErrorOr<DesktopSalesListResult>>
{
    public async Task<ErrorOr<DesktopSalesListResult>> Handle(
        GetDesktopSalesQuery request, CancellationToken cancellationToken)
    {
        var query = db.DesktopSales
            .AsNoTracking()
            .Include(s => s.Lines)
            .AsQueryable();

        if (!string.IsNullOrEmpty(request.WarehouseCode))
            query = query.Where(s => s.WarehouseCode == request.WarehouseCode);

        if (!string.IsNullOrEmpty(request.CardCode))
            query = query.Where(s => s.CardCode == request.CardCode);

        if (!string.IsNullOrEmpty(request.ConsolidationStatus) &&
            Enum.TryParse<DesktopSaleConsolidationStatus>(request.ConsolidationStatus, true, out var status))
            query = query.Where(s => s.ConsolidationStatus == status);

        if (request.FromDate.HasValue)
            query = query.Where(s => s.DocDate >= request.FromDate.Value.Date);

        if (request.ToDate.HasValue)
            query = query.Where(s => s.DocDate <= request.ToDate.Value.Date);

        var totalCount = await query.CountAsync(cancellationToken);

        var sales = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(s => new DesktopSaleListItemDto(
                s.Id,
                s.ExternalReferenceId,
                s.SourceSystem,
                s.CardCode,
                s.CardName,
                s.DocDate,
                s.TotalAmount,
                s.VatAmount,
                s.Currency,
                s.FiscalizationStatus.ToString(),
                s.FiscalReceiptNumber,
                s.ConsolidationStatus.ToString(),
                s.ConsolidationId,
                s.WarehouseCode,
                s.PaymentMethod,
                s.PaymentReference,
                s.AmountPaid,
                s.CreatedBy,
                s.CreatedAt,
                s.Lines.Select(l => new DesktopSaleLineItemDto(
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.Quantity,
                    l.UnitPrice,
                    l.LineTotal,
                    l.WarehouseCode,
                    l.TaxCode,
                    l.DiscountPercent
                )).ToList()
            ))
            .ToListAsync(cancellationToken);

        return new DesktopSalesListResult(
            sales,
            totalCount,
            request.Page,
            request.PageSize,
            (request.Page * request.PageSize) < totalCount
        );
    }
}
