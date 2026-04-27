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
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var fromDate = NormalizeUtcDate(request.FromDate);
        var toExclusive = NormalizeUtcDate(request.ToDate)?.AddDays(1);
        var search = NormalizeSearchValue(request.Search);

        var query = context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Source == SalesOrderSource.Mobile && o.CreatedByUserId == request.UserId);

        if (request.Status.HasValue)
            query = query.Where(o => o.Status == request.Status.Value);

        if (fromDate.HasValue)
            query = query.Where(o => o.OrderDate >= fromDate.Value);

        if (toExclusive.HasValue)
            query = query.Where(o => o.OrderDate < toExclusive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchPattern = $"%{search}%";
            if (TryParseOrderNumber(search, out var docNumber))
            {
                query = query.Where(o =>
                    EF.Functions.ILike(o.OrderNumber, searchPattern) ||
                    (o.CustomerRefNo != null && EF.Functions.ILike(o.CustomerRefNo, searchPattern)) ||
                    o.SAPDocNum == docNumber ||
                    o.SAPDocEntry == docNumber);
            }
            else
            {
                query = query.Where(o =>
                    EF.Functions.ILike(o.OrderNumber, searchPattern) ||
                    (o.CustomerRefNo != null && EF.Functions.ILike(o.CustomerRefNo, searchPattern)));
            }
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var orders = await query
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new SalesOrderDto
            {
                Id = o.Id,
                SAPDocEntry = o.SAPDocEntry,
                SAPDocNum = o.SAPDocNum,
                OrderNumber = o.OrderNumber,
                OrderDate = o.OrderDate,
                DeliveryDate = o.DeliveryDate,
                CardCode = o.CardCode,
                CardName = o.CardName,
                CustomerRefNo = o.CustomerRefNo,
                Status = o.Status,
                Comments = o.Comments,
                Currency = o.Currency,
                SubTotal = o.SubTotal,
                TaxAmount = o.TaxAmount,
                DocTotal = o.DocTotal,
                CreatedAt = o.CreatedAt,
                UpdatedAt = o.UpdatedAt,
                IsSynced = o.IsSynced,
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                Latitude = o.Latitude,
                Longitude = o.Longitude
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

    private static DateTime? NormalizeUtcDate(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }

    private static string? NormalizeSearchValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryParseOrderNumber(string search, out int docNumber)
    {
        var normalized = search.Trim();
        if (normalized.StartsWith("SAP-", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];

        return int.TryParse(normalized, out docNumber);
    }
}
