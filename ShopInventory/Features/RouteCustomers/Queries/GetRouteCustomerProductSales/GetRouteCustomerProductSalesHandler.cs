using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductSales;

/// <summary>
/// Every customer's purchases of every item, grouped in the database off the two line tables.
///
/// The same two sources and the same filters as the product mix, keyed one level finer: by customer as
/// well as by item. A depot's month is a few thousand lines and comes back as a few hundred rows.
/// </summary>
public sealed class GetRouteCustomerProductSalesHandler(
    ApplicationDbContext db
) : IRequestHandler<GetRouteCustomerProductSalesQuery, ErrorOr<RouteCustomerProductSalesDto>>
{
    private sealed record LineAggregate(
        string CardCode,
        int? RouteCustomerId,
        string RouteCustomerCode,
        string ItemCode,
        string? ItemDescription,
        string? UoMCode,
        string Currency,
        decimal Quantity,
        decimal LineTotal,
        int LineCount,
        DateTime LastSoldAt);

    public async Task<ErrorOr<RouteCustomerProductSalesDto>> Handle(
        GetRouteCustomerProductSalesQuery query,
        CancellationToken cancellationToken)
    {
        var (from, to) = RouteCustomerSalesReporting.NormalizeWindow(query.From, query.To);
        var (fromUtc, toUtcExclusive) = RouteCustomerSalesReporting.ToUtcWindow(from, to);

        var partner = string.IsNullOrWhiteSpace(query.AssignedBusinessPartnerCode)
            ? null
            : query.AssignedBusinessPartnerCode.Trim();

        var vendingDepotCodes = await RouteCustomerScopes.VendingDepotCodesAsync(db, query.Scope, cancellationToken);

        var aggregates = await LoadOfflineAsync(partner, from, to, cancellationToken);
        aggregates.AddRange(await LoadOnlineAsync(partner, fromUtc, toUtcExclusive, cancellationToken));

        // Decided on the partner the sale was booked against, the same thing the summary decides on, so
        // the two reads cannot disagree about whose sale it was.
        aggregates = aggregates
            .Where(aggregate => RouteCustomerScopes.Includes(query.Scope, vendingDepotCodes, aggregate.CardCode))
            .ToList();

        var resolveId = await LoadCustomerResolverAsync(partner, cancellationToken);

        var rows = aggregates
            .Select(aggregate => aggregate with
            {
                RouteCustomerId = aggregate.RouteCustomerId ?? resolveId(aggregate.RouteCustomerCode, aggregate.CardCode)
            })
            // A customer's sales can arrive under its id and under its bare code, and an item's lines
            // under more than one spelling of its description. Both fold into one row here.
            .GroupBy(aggregate => (
                Card: Normalize(aggregate.CardCode),
                aggregate.RouteCustomerId,
                Code: Normalize(aggregate.RouteCustomerCode),
                Item: Normalize(aggregate.ItemCode),
                aggregate.Currency))
            .Select(group => new RouteCustomerProductSalesRowDto
            {
                AssignedBusinessPartnerCode = group.First().CardCode,
                RouteCustomerId = group.Key.RouteCustomerId,
                RouteCustomerCode = group.First().RouteCustomerCode,
                ItemCode = group.First().ItemCode,
                ItemDescription = group
                    .Select(aggregate => aggregate.ItemDescription)
                    .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)),
                UoMCode = group
                    .Select(aggregate => aggregate.UoMCode)
                    .FirstOrDefault(uom => !string.IsNullOrWhiteSpace(uom)),
                Currency = group.Key.Currency,
                Quantity = group.Sum(aggregate => aggregate.Quantity),
                LineTotal = group.Sum(aggregate => aggregate.LineTotal),
                LineCount = group.Sum(aggregate => aggregate.LineCount),
                LastSoldAt = group.Max(aggregate => aggregate.LastSoldAt)
            })
            .OrderBy(row => row.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RouteCustomerCode, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.LineTotal)
            .ThenBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RouteCustomerProductSalesDto
        {
            From = from,
            To = to,
            Rows = rows
        };
    }

    private async Task<List<LineAggregate>> LoadOfflineAsync(
        string? partner,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var linesQuery = db.DesktopSaleLines
            .AsNoTracking()
            // An online van sale's lines are read from its confirmed reservation below. Its row here is a
            // copy of the same cart, kept for the receipt, and reading both would double it.
            .Where(line => line.Sale.SourceSystem != SaleSourceSystems.VanSalesOnline)
            .Where(line => line.Sale.RouteCustomerCode != null)
            .Where(line => line.Sale.DocDate >= from && line.Sale.DocDate <= to);

        if (partner is not null)
        {
            linesQuery = linesQuery.Where(line => line.Sale.CardCode == partner);
        }

        var grouped = await linesQuery
            .GroupBy(line => new
            {
                line.Sale.CardCode,
                line.Sale.RouteCustomerId,
                line.Sale.RouteCustomerCode,
                line.ItemCode,
                line.ItemDescription,
                line.UoMCode,
                line.Sale.Currency
            })
            .Select(group => new
            {
                group.Key,
                Quantity = group.Sum(line => line.Quantity),
                LineTotal = group.Sum(line => line.LineTotal),
                LineCount = group.Count(),
                LastSoldAt = group.Max(line => line.Sale.DocDate)
            })
            .ToListAsync(cancellationToken);

        return grouped.Select(row => new LineAggregate(
            row.Key.CardCode,
            row.Key.RouteCustomerId,
            row.Key.RouteCustomerCode ?? string.Empty,
            row.Key.ItemCode,
            row.Key.ItemDescription,
            row.Key.UoMCode,
            RouteCustomerSalesReporting.NormalizeCurrency(row.Key.Currency),
            row.Quantity,
            row.LineTotal,
            row.LineCount,
            row.LastSoldAt.Date)).ToList();
    }

    private async Task<List<LineAggregate>> LoadOnlineAsync(
        string? partner,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var linesQuery = db.StockReservationLines
            .AsNoTracking()
            .Where(line => line.Reservation.RouteCustomerCode != null)
            .Where(line => line.Reservation.Status == ReservationStatus.Confirmed)
            .Where(line => line.Reservation.CreatedAt >= fromUtc && line.Reservation.CreatedAt < toUtcExclusive);

        if (partner is not null)
        {
            linesQuery = linesQuery.Where(line => line.Reservation.CardCode == partner);
        }

        var grouped = await linesQuery
            .GroupBy(line => new
            {
                line.Reservation.CardCode,
                line.Reservation.RouteCustomerId,
                line.Reservation.RouteCustomerCode,
                line.ItemCode,
                line.ItemDescription,
                line.UoMCode,
                line.Reservation.Currency
            })
            .Select(group => new
            {
                group.Key,
                Quantity = group.Sum(line => line.ReservedQuantity),
                LineTotal = group.Sum(line => line.LineTotal),
                LineCount = group.Count(),
                LastSoldAt = group.Max(line => line.Reservation.CreatedAt)
            })
            .ToListAsync(cancellationToken);

        return grouped.Select(row => new LineAggregate(
            row.Key.CardCode,
            row.Key.RouteCustomerId,
            row.Key.RouteCustomerCode ?? string.Empty,
            row.Key.ItemCode,
            row.Key.ItemDescription,
            row.Key.UoMCode,
            RouteCustomerSalesReporting.NormalizeCurrency(row.Key.Currency),
            row.Quantity,
            row.LineTotal,
            row.LineCount,
            RouteCustomerSalesReporting.TradingDayOf(row.LastSoldAt))).ToList();
    }

    /// <summary>
    /// Finds the live customer a sale with no id belongs to, by its code under the partner it was booked
    /// against. Code alone would not do: codes are unique only within a partner.
    /// </summary>
    private async Task<Func<string, string, int?>> LoadCustomerResolverAsync(
        string? partner,
        CancellationToken cancellationToken)
    {
        var customersQuery = db.RouteCustomers.AsNoTracking();

        if (partner is not null)
        {
            customersQuery = customersQuery.Where(customer => customer.AssignedBusinessPartnerCode == partner);
        }

        var customers = await customersQuery
            .Select(customer => new { customer.Id, customer.Code, customer.AssignedBusinessPartnerCode })
            .ToListAsync(cancellationToken);

        var byCodeAndPartner = customers
            .GroupBy(customer => (Normalize(customer.Code), Normalize(customer.AssignedBusinessPartnerCode)))
            .ToDictionary(group => group.Key, group => group.Min(customer => customer.Id));

        return (code, cardCode) =>
            byCodeAndPartner.TryGetValue((Normalize(code), Normalize(cardCode)), out var id) ? id : null;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
