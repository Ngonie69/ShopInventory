using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
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
    ApplicationDbContext db,
    ISaleInvoiceCostReader? costReader = null,
    ILogger<GetRouteCustomerProductMixHandler>? logger = null
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

        /// <summary>The SAP invoices this aggregate's posted lines are on, for their booked cost.</summary>
        public List<(int DocEntry, string? WarehouseCode)> Invoices { get; init; } = [];
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

        // A named customer already is whichever population it belongs to, so the scope is only applied
        // when the caller asked across customers. Sales are matched on the business partner they were
        // booked against, the same thing RouteCustomerScopes decides on.
        var vendingDepotCodes = customer is null
            ? (await RouteCustomerScopes.VendingDepotCodesAsync(db, query.Scope, cancellationToken)).ToList()
            : [];

        var aggregates = await LoadOfflineAsync(customer, route, query.Scope, vendingDepotCodes, from, to, cancellationToken);
        aggregates.AddRange(await LoadOnlineAsync(customer, route, query.Scope, vendingDepotCodes, fromUtc, toUtcExclusive, cancellationToken));

        var (margin, marginStatus) = await ManagementSalesRollup.MarginOfInvoicesAsync(
            aggregates.SelectMany(aggregate => aggregate.Invoices),
            costReader,
            (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            cancellationToken);

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
                // Costed once per currency over all its lines' invoices: two aggregates of one item can
                // share an invoice, and the margin book counts each (invoice, item) once.
                TotalsByCurrency = RouteCustomerSalesReporting.SumByCurrency(group.Select(aggregate =>
                        new RouteCustomerSalesTotalsDto
                        {
                            Currency = aggregate.Currency,
                            SaleCount = aggregate.LineCount,
                            Gross = aggregate.Gross
                        }))
                    .Select(total => total.WithMargin(margin.ForInvoiceItems(group
                        .Where(aggregate => string.Equals(aggregate.Currency, total.Currency, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(aggregate => aggregate.Invoices)
                        .Select(invoice => ((int?)invoice.DocEntry, group.Key)))))
                    .ToList()
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
            Items = items,
            Margin = marginStatus
        };
    }

    private async Task<List<ItemAggregate>> LoadOfflineAsync(
        RouteCustomerEntity? customer,
        string? route,
        RouteCustomerScope scope,
        List<string> vendingDepotCodes,
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
        else
        {
            if (route is not null)
            {
                linesQuery = linesQuery.Where(line => line.Sale.CardCode == route);
            }

            if (vendingDepotCodes.Count > 0)
            {
                linesQuery = scope == RouteCustomerScope.Vending
                    ? linesQuery.Where(line => vendingDepotCodes.Contains(line.Sale.CardCode))
                    : linesQuery.Where(line => !vendingDepotCodes.Contains(line.Sale.CardCode));
            }
            else if (scope == RouteCustomerScope.Vending)
            {
                linesQuery = linesQuery.Where(_ => false);
            }
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

        var invoices = (await linesQuery
                .Where(line => line.Sale.SapDocEntry != null)
                .Select(line => new
                {
                    line.ItemCode,
                    line.ItemDescription,
                    line.UoMCode,
                    line.Sale.Currency,
                    line.Sale.RouteCustomerCode,
                    DocEntry = line.Sale.SapDocEntry!.Value,
                    line.Sale.WarehouseCode
                })
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToLookup(
                line => (line.ItemCode, line.ItemDescription, line.UoMCode, line.Currency, line.RouteCustomerCode),
                line => (line.DocEntry, (string?)line.WarehouseCode));

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
            CustomerCodes = row.Key.RouteCustomerCode is null ? [] : [row.Key.RouteCustomerCode],
            Invoices = invoices[(row.Key.ItemCode, row.Key.ItemDescription, row.Key.UoMCode, row.Key.Currency, row.Key.RouteCustomerCode)].ToList()
        }).ToList();
    }

    private async Task<List<ItemAggregate>> LoadOnlineAsync(
        RouteCustomerEntity? customer,
        string? route,
        RouteCustomerScope scope,
        List<string> vendingDepotCodes,
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
        else
        {
            if (route is not null)
            {
                linesQuery = linesQuery.Where(line => line.Reservation.CardCode == route);
            }

            if (vendingDepotCodes.Count > 0)
            {
                linesQuery = scope == RouteCustomerScope.Vending
                    ? linesQuery.Where(line => vendingDepotCodes.Contains(line.Reservation.CardCode))
                    : linesQuery.Where(line => !vendingDepotCodes.Contains(line.Reservation.CardCode));
            }
            else if (scope == RouteCustomerScope.Vending)
            {
                linesQuery = linesQuery.Where(_ => false);
            }
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

        var invoices = (await linesQuery
                .Where(line => line.Reservation.SAPDocEntry != null)
                .Select(line => new
                {
                    line.ItemCode,
                    line.ItemDescription,
                    line.UoMCode,
                    line.Reservation.Currency,
                    line.Reservation.RouteCustomerCode,
                    DocEntry = line.Reservation.SAPDocEntry!.Value,
                    line.WarehouseCode
                })
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToLookup(
                line => (line.ItemCode, line.ItemDescription, line.UoMCode, line.Currency, line.RouteCustomerCode),
                line => (line.DocEntry, line.WarehouseCode));

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
            CustomerCodes = row.Key.RouteCustomerCode is null ? [] : [row.Key.RouteCustomerCode],
            Invoices = invoices[(row.Key.ItemCode, row.Key.ItemDescription, row.Key.UoMCode, row.Key.Currency, row.Key.RouteCustomerCode)].ToList()
        }).ToList();
    }
}
