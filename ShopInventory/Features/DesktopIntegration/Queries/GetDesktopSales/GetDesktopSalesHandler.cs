using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
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

        // The source scope, decided rather than inherited. This is a list of sales, and an online van
        // sale's row is not one — it carries the receipt a handset signed for a sale that lives in SAP
        // and in its confirmed StockReservation. Every money column on this DTO therefore describes a
        // sale the caller is already looking at somewhere else, so the default answer excludes it and a
        // caller that wants those rows asks for them by name.
        //
        // Findable rather than hidden: the row is real, an operator chasing a fiscal reference has to be
        // able to reach it, and the fiscalisation console is not a document list. sourceSystem is a plain
        // equality filter so `?sourceSystem=KefalosVanSalesOnline` returns exactly them.
        if (!string.IsNullOrWhiteSpace(request.SourceSystem))
        {
            var sourceSystem = request.SourceSystem.Trim();
            query = query.Where(s => s.SourceSystem == sourceSystem);
        }
        else
        {
            query = query.Where(s => s.SourceSystem != SaleSourceSystems.VanSalesOnline);
        }

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
