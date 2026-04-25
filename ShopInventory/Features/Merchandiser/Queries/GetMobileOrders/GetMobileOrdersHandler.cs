using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrders;

public sealed class GetMobileOrdersHandler(
    ApplicationDbContext context,
    IAuditService auditService
) : IRequestHandler<GetMobileOrdersQuery, ErrorOr<SalesOrderListResponseDto>>
{
    public async Task<ErrorOr<SalesOrderListResponseDto>> Handle(
        GetMobileOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 50);

        var query = context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Source == SalesOrderSource.Mobile && o.CreatedByUserId == request.UserId);

        if (request.Status.HasValue)
            query = query.Where(o => o.Status == request.Status.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var orders = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(o => o.Lines)
            .Select(o => new SalesOrderDto
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                OrderDate = o.OrderDate,
                DeliveryDate = o.DeliveryDate,
                CardCode = o.CardCode,
                CardName = o.CardName,
                Status = o.Status,
                Comments = o.Comments,
                Currency = o.Currency,
                SubTotal = o.SubTotal,
                TaxAmount = o.TaxAmount,
                DocTotal = o.DocTotal,
                CreatedAt = o.CreatedAt,
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                Lines = o.Lines.Select(l => new SalesOrderLineDto
                {
                    Id = l.Id,
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    LineTotal = l.LineTotal,
                    UoMCode = l.UoMCode
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        var response = new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            Orders = orders
        };

        try
        {
            var statusLabel = request.Status?.ToString() ?? "All";
            await auditService.LogAsync(
                AuditActions.ViewMobileOrders,
                "SalesOrder",
                null,
                $"Viewed mobile orders page {page} (size {pageSize}, status {statusLabel}). Returned {orders.Count} of {totalCount} orders.",
                true);
        }
        catch
        {
        }

        return response;
    }
}
