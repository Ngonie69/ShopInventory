using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;

public sealed class GetDesktopSalesHandler(ApplicationDbContext db, IAuditService auditService)
    : IRequestHandler<GetDesktopSalesQuery, ErrorOr<DesktopSalesListResult>>
{
    /// <summary>
    /// Lists sales, and records whose takings were read.
    /// </summary>
    /// <remarks>
    /// One of the two desktop reads the blanket filter deliberately leaves to its handler. This one
    /// shows a shop's money, and the scope resolver below exists because the warehouse used to arrive
    /// unchecked from the query string — so a refused read is exactly the event worth keeping: it is
    /// either a client bug or somebody reaching for another shop's takings, and nothing else records
    /// that it happened.
    /// </remarks>
    public async Task<ErrorOr<DesktopSalesListResult>> Handle(
        GetDesktopSalesQuery request, CancellationToken cancellationToken)
    {
        var outcome = await ReadAsync(request, cancellationToken);

        await auditService.LogAsync(
            AuditActions.ViewDesktopSales,
            nameof(DesktopSaleEntity),
            string.IsNullOrWhiteSpace(request.WarehouseCode) ? "(caller scope)" : request.WarehouseCode.Trim(),
            outcome.IsError
                ? $"Refused a sales read for warehouse {request.WarehouseCode}."
                : $"Read {outcome.Value.Sales.Count} of {outcome.Value.TotalCount} sale(s), "
                    + $"page {outcome.Value.Page}.",
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<ErrorOr<DesktopSalesListResult>> ReadAsync(
        GetDesktopSalesQuery request, CancellationToken cancellationToken)
    {
        // Resolved here rather than in the controller so that no caller can reach these rows without
        // being scoped. The warehouse used to arrive from the query string unchecked.
        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == request.CallerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var requestedWarehouse = string.IsNullOrWhiteSpace(request.WarehouseCode)
            ? null
            : request.WarehouseCode.Trim();

        var warehouseFilter = requestedWarehouse;

        if (!scope.Value.IsUnrestricted)
        {
            var assigned = scope.Value.WarehouseCode!;

            // Refused rather than narrowed. A till rendering a page headed with one warehouse and
            // filled with another's takings is worse than an error, and silently rewriting the request
            // would hide a client bug — or a probe — that somebody should see.
            if (requestedWarehouse is not null &&
                !string.Equals(requestedWarehouse, assigned, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.DesktopSales.SalesReadOutsideScope(requestedWarehouse, assigned);
            }

            // Applied whether or not the caller asked for one, so omitting the parameter narrows to the
            // caller's own shop rather than widening to every shop.
            warehouseFilter = assigned;
        }

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

        if (warehouseFilter is not null)
            query = query.Where(s => s.WarehouseCode == warehouseFilter);

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

        // Projected in two steps rather than one. The posting-eligibility rule is a method — it has
        // to be, because the console and the posting command must refuse identically — and a method
        // cannot be translated to SQL, so the enums it reads are carried out of the database
        // alongside the row and the rule is applied to the page in memory.
        var rows = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(s => new
            {
                s.ConsolidationStatus,
                s.FiscalizationStatus,
                Sale = new DesktopSaleListItemDto(
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
                s.FiscalQRCode,
                s.FiscalVerificationCode,
                s.FiscalDeviceNumber,
                s.FiscalDayNo,
                s.ConsolidationStatus.ToString(),
                s.ConsolidationId,
                s.WarehouseCode,
                s.PaymentMethod,
                s.PaymentReference,
                s.AmountPaid,
                s.CreatedBy,
                s.CreatedAt,
                s.SapDocEntry,
                s.SapDocNum,
                s.PostedAt,
                s.PostingAttempts,
                s.LastPostingError,
                s.PaymentStatus,
                s.PaymentSapDocNum,
                // Filled in below, where the rule can actually be called.
                null,
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
                )).ToList())
            })
            .ToListAsync(cancellationToken);

        var sales = rows
            .Select(row => row.Sale with
            {
                PostRefusal = DesktopSalePostEligibility.Refusal(
                    row.Sale.SourceSystem, row.ConsolidationStatus, row.FiscalizationStatus)
            })
            .ToList();

        return new DesktopSalesListResult(
            sales,
            totalCount,
            request.Page,
            request.PageSize,
            (request.Page * request.PageSize) < totalCount
        );
    }
}
