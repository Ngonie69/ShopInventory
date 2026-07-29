using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderHistory;

public sealed class GetVanSalesSalesOrderHistoryHandler(
    ApplicationDbContext db,
    ILogger<GetVanSalesSalesOrderHistoryHandler> logger
) : IRequestHandler<GetVanSalesSalesOrderHistoryQuery, ErrorOr<List<VanSalesLegacyOrderDto>>>
{
    public async Task<ErrorOr<List<VanSalesLegacyOrderDto>>> Handle(
        GetVanSalesSalesOrderHistoryQuery query,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query.Request.Type) &&
            !string.Equals(query.Request.Type, "SO", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidOrderType",
                "Only sales-order history is available from the van sales sales-order history endpoint.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var fromDateUtc = VanSalesCompatibilityMapper.ParseLegacyDate(query.Request.StartDate)?.Date;
        var toDateUtcExclusive = VanSalesCompatibilityMapper.ParseLegacyDate(query.Request.EndDate)?.Date.AddDays(1);

        var salesOrdersQuery = db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Source == SalesOrderSource.Mobile && order.CreatedByUserId == user.Id);

        if (effectiveCustomerCodes.Count > 0)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => effectiveCustomerCodes.Contains(order.CardCode));
        }

        if (fromDateUtc.HasValue)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate >= fromDateUtc.Value);
        }

        if (toDateUtcExclusive.HasValue)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate < toDateUtcExclusive.Value);
        }

        var orders = await salesOrdersQuery
            .OrderByDescending(order => order.OrderDate)
            .ThenByDescending(order => order.Id)
            .Select(order => new SalesOrderDto
            {
                Id = order.Id,
                SAPDocEntry = order.SAPDocEntry,
                SAPDocNum = order.SAPDocNum,
                OrderNumber = order.OrderNumber,
                OrderDate = order.OrderDate,
                DeliveryDate = order.DeliveryDate,
                CardCode = order.CardCode,
                CardName = order.CardName,
                Currency = order.Currency,
                TaxAmount = order.TaxAmount,
                DocTotal = order.DocTotal,
                CreatedAt = order.CreatedAt,
                ApprovedDate = order.ApprovedDate,
                InvoiceSapDocNum = order.Invoice != null ? order.Invoice.SAPDocNum : null,
                Status = order.Status,
                Lines = order.Lines
                    .OrderBy(line => line.LineNum)
                    .Select(line => new SalesOrderLineDto
                    {
                        Id = line.Id,
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode,
                        ItemDescription = line.ItemDescription,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return orders
            .Select(VanSalesCompatibilityMapper.MapLegacySalesOrder)
            .ToList();
    }
}