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

        // An empty scope returns nothing, never everything — the same rule the invoice half of
        // GetVanSalesOrderHistoryHandler states, and for the same reason. The customer scope is now
        // the only narrowing on this read, so "no codes means no filter" would hand a handset in a
        // van every sales order the company holds in the window.
        if (effectiveCustomerCodes.Count == 0)
        {
            logger.LogWarning(
                "Van sales user {UserId} has no customer scope, so no sales-order history can be read for them",
                user.Id);

            return new List<VanSalesLegacyOrderDto>();
        }

        var window = VanSalesLegacyDateWindow.Parse(query.Request.StartDate, query.Request.EndDate);

        // Scoped to the business partner, not to the rep who keyed it. This screen exists so a sales
        // rep can convert an order that already stands against a shop they call on, and who raised it
        // is not what decides that: an order taken by whoever had the handset yesterday, or keyed at
        // the depot, is the same shop's order and the same document to convert. Filtering on
        // CreatedByUserId meant the only orders a rep could ever convert were the ones they had
        // personally keyed on that handset, which is not a route's paperwork but one person's.
        //
        // Source is not filtered either, for the same reason: Web and VanSalesCustomer orders against
        // an assigned shop are that shop's demand just as much as a Mobile one, and the ordering app's
        // whole point is that the shop raises them itself.
        //
        // Status is deliberately not filtered, and it was tried the other way round. Conversion takes
        // approved orders only, so listing just those looks like the tidy thing to do — but an order
        // is written Pending and nothing on the handset approves it, so an Approved-only list is empty
        // of the order the rep raised a minute ago. Measured rather than reasoned: SO3442, raised from
        // a handset on 2026-09-09, stored as status 1.
        //
        // What conversion will refuse is a real mismatch, and this is the wrong place to answer it:
        // hiding the document loses a rep the record of an order they took, which is most of what this
        // screen is for. It belongs on the row, as the order's status, where a rep can read it before
        // tapping convert.
        var salesOrdersQuery = db.SalesOrders
            .AsNoTracking()
            .Where(order => effectiveCustomerCodes.Contains(order.CardCode));

        // OrderDate is timestamptz, so the trading days the handset asked for are compared as the UTC
        // instants they cover rather than as bare dates.
        if (window.FromUtc is { } fromUtc)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate >= fromUtc);
        }

        if (window.ToUtcExclusive is { } toUtcExclusive)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate < toUtcExclusive);
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