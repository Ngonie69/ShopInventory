using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductMix;

/// <summary>
/// What route customers buy, one row per item.
///
/// Grouped in the database off the line tables, because a route's mix over a quarter is tens of thousands
/// of lines and only a few hundred items.
/// </summary>
public sealed class GetRouteCustomerProductMixHandler(
    ApplicationDbContext db
) : IRequestHandler<GetRouteCustomerProductMixQuery, ErrorOr<RouteCustomerProductMixDto>>
{
    private sealed class ItemAggregate
    {
        public string ItemCode { get; init; } = string.Empty;
        public string? ItemDescription { get; init; }
        public string? UoMCode { get; init; }
        public string Currency { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal Gross { get; init; }
        public int LineCount { get; init; }
        public DateTime LastSoldAt { get; init; }
        public IReadOnlyCollection<string> CustomerCodes { get; init; } = [];
    }

    public async Task<ErrorOr<RouteCustomerProductMixDto>> Handle(
        GetRouteCustomerProductMixQuery query,
        CancellationToken cancellationToken)
    {
        RouteCustomerEntity? customer = null;

        if (query.RouteCustomerId is { } routeCustomerId)
        {
            customer = await db.RouteCustomers
                .AsNoTracking()
                .FirstOrDefaultAsync(entity => entity.Id == routeCustomerId, cancellationToken);

            if (customer is null)
            {
                return Errors.RouteCustomers.NotFound(routeCustomerId);
            }
        }

        var (from, to) = RouteCustomerSalesReporting.NormalizeWindow(query.From, query.To);
        var (fromUtc, toUtcExclusive) = RouteCustomerSalesReporting.ToUtcWindow(from, to);

        var route = string.IsNullOrWhiteSpace(query.AssignedBusinessPartnerCode)
            ? customer?.AssignedBusinessPartnerCode
            : query.AssignedBusinessPartnerCode.Trim();

        var aggregates = await LoadOfflineAsync(customer, route, from, to, cancellationToken);
        aggregates.AddRange(await LoadOnlineAsync(customer, route, fromUtc, toUtcExclusive, cancellationToken));

        var items = aggregates
            .GroupBy(aggregate => aggregate.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouteCustomerProductMixRowDto
            {
                ItemCode = group.Key,
                ItemDescription = group
                    .Select(aggregate => aggregate.ItemDescription)
                    .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)),
                // Safe within a row: every line in this group is the same item. Across rows it would be
                // adding cases to bottles, which is why nothing here totals the column.
                Quantity = group.Sum(aggregate => aggregate.Quantity),
                UoMCode = group
                    .Select(aggregate => aggregate.UoMCode)
                    .FirstOrDefault(uom => !string.IsNullOrWhiteSpace(uom)),
                LineCount = group.Sum(aggregate => aggregate.LineCount),
                CustomerCount = group
                    .SelectMany(aggregate => aggregate.CustomerCodes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                LastSoldAt = group.Max(aggregate => aggregate.LastSoldAt),
                TotalsByCurrency = RouteCustomerSalesReporting.SumByCurrency(group.Select(aggregate =>
                    new RouteCustomerSalesTotalsDto
                    {
                        Currency = aggregate.Currency,
                        SaleCount = aggregate.LineCount,
                        Gross = aggregate.Gross
                    }))
            })
            // Ranked by how often the item is bought, not by value: value would mean adding USD to ZWG
            // to get a sort key, and quantity would rank a case against a bottle.
            .OrderByDescending(row => row.LineCount)
            .ThenByDescending(row => row.Quantity)
            .ThenBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (query.Top > 0)
        {
            items = items.Take(query.Top).ToList();
        }

        return new RouteCustomerProductMixDto
        {
            From = from,
            To = to,
            AssignedBusinessPartnerCode = route,
            RouteCustomerId = customer?.Id,
            Items = items
        };
    }

    private async Task<List<ItemAggregate>> LoadOfflineAsync(
        RouteCustomerEntity? customer,
        string? route,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var linesQuery = db.DesktopSaleLines
            .AsNoTracking()
            // An online van sale's lines are read from its confirmed reservation by LoadOnlineAsync. It
            // also leaves a row on this table, carrying the receipt the handset signed, whose lines are a
            // copy of the same cart — reading both would double every item an online sale moved.
            .Where(line => line.Sale.SourceSystem != SaleSourceSystems.VanSalesOnline)
            .Where(line => line.Sale.RouteCustomerCode != null)
            .Where(line => line.Sale.DocDate >= from && line.Sale.DocDate <= to);

        if (customer is not null)
        {
            linesQuery = linesQuery.Where(line => line.Sale.RouteCustomerId == customer.Id ||
                (line.Sale.RouteCustomerId == null &&
                 line.Sale.RouteCustomerCode == customer.Code &&
                 line.Sale.CardCode == customer.AssignedBusinessPartnerCode));
        }
        else if (route is not null)
        {
            linesQuery = linesQuery.Where(line => line.Sale.CardCode == route);
        }

        var grouped = await linesQuery
            .GroupBy(line => new
            {
                line.ItemCode,
                line.ItemDescription,
                line.UoMCode,
                line.Sale.Currency,
                line.Sale.RouteCustomerCode
            })
            .Select(group => new
            {
                group.Key,
                Quantity = group.Sum(line => line.Quantity),
                Gross = group.Sum(line => line.LineTotal),
                LineCount = group.Count(),
                LastSoldAt = group.Max(line => line.Sale.DocDate)
            })
            .ToListAsync(cancellationToken);

        return grouped.Select(row => new ItemAggregate
        {
            ItemCode = row.Key.ItemCode,
            ItemDescription = row.Key.ItemDescription,
            UoMCode = row.Key.UoMCode,
            Currency = RouteCustomerSalesReporting.NormalizeCurrency(row.Key.Currency),
            Quantity = row.Quantity,
            Gross = row.Gross,
            LineCount = row.LineCount,
            LastSoldAt = row.LastSoldAt,
            CustomerCodes = row.Key.RouteCustomerCode is null ? [] : [row.Key.RouteCustomerCode]
        }).ToList();
    }

    private async Task<List<ItemAggregate>> LoadOnlineAsync(
        RouteCustomerEntity? customer,
        string? route,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var linesQuery = db.StockReservationLines
            .AsNoTracking()
            .Where(line => line.Reservation.RouteCustomerCode != null)
            .Where(line => line.Reservation.Status == ReservationStatus.Confirmed)
            .Where(line => line.Reservation.CreatedAt >= fromUtc && line.Reservation.CreatedAt < toUtcExclusive);

        if (customer is not null)
        {
            linesQuery = linesQuery.Where(line => line.Reservation.RouteCustomerId == customer.Id ||
                (line.Reservation.RouteCustomerId == null &&
                 line.Reservation.RouteCustomerCode == customer.Code &&
                 line.Reservation.CardCode == customer.AssignedBusinessPartnerCode));
        }
        else if (route is not null)
        {
            linesQuery = linesQuery.Where(line => line.Reservation.CardCode == route);
        }

        var grouped = await linesQuery
            .GroupBy(line => new
            {
                line.ItemCode,
                line.ItemDescription,
                line.UoMCode,
                line.Reservation.Currency,
                line.Reservation.RouteCustomerCode
            })
            .Select(group => new
            {
                group.Key,
                Quantity = group.Sum(line => line.ReservedQuantity),
                Gross = group.Sum(line => line.LineTotal),
                LineCount = group.Count(),
                LastSoldAt = group.Max(line => line.Reservation.CreatedAt)
            })
            .ToListAsync(cancellationToken);

        return grouped.Select(row => new ItemAggregate
        {
            ItemCode = row.Key.ItemCode,
            ItemDescription = row.Key.ItemDescription,
            UoMCode = row.Key.UoMCode,
            Currency = RouteCustomerSalesReporting.NormalizeCurrency(row.Key.Currency),
            Quantity = row.Quantity,
            Gross = row.Gross,
            LineCount = row.LineCount,
            LastSoldAt = Services.AuditService.ToCAT(row.LastSoldAt).Date,
            CustomerCodes = row.Key.RouteCustomerCode is null ? [] : [row.Key.RouteCustomerCode]
        }).ToList();
    }
}
