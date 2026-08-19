using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;

/// <summary>
/// Every route customer against what they bought, grouped by route.
///
/// The money is aggregated in the database — one grouped query per source, per measure — rather than by
/// reading sales into memory and adding them up here. A route summary with no filters spans every van and
/// a quarter of trading, and materialising that to count it would be the difference between a page and a
/// timeout.
///
/// The customers come from their own table and the aggregates are attached to them, not the other way
/// round. A customer who bought nothing has no row in any sales table, and they are exactly who the
/// dormancy view is looking for.
///
/// One read here deliberately ignores the window: the first and last day each customer ever bought. The
/// window answers "who is trading and for how much", and it cannot answer "who has stopped" — a shop
/// whose last purchase is older than the window has no row in any windowed aggregate, which is
/// indistinguishable from a shop that has never bought at all. Those are opposite findings: one needs
/// winning back and the other has never been converted. <see cref="LoadLifetimeSpansAsync"/> is what
/// tells them apart, so <c>FirstSaleAt</c>, <c>LastSaleAt</c> and <c>DaysSinceLastSale</c> are all-time
/// while every count and total beside them belongs to the window.
/// </summary>
public sealed class GetRouteCustomerSalesSummaryHandler(
    ApplicationDbContext db
) : IRequestHandler<GetRouteCustomerSalesSummaryQuery, ErrorOr<RouteCustomerSalesSummaryDto>>
{
    /// <summary>
    /// How a sale is tied back to a customer. The id is the real link; the code and route are the
    /// fallback for sales whose customer record has since been deleted. Code alone would not do — route
    /// customer codes are unique only within a route, so two vans can each have a "TEST".
    /// </summary>
    private readonly record struct CustomerKey(int? RouteCustomerId, string Code, string RouteCode);

    private sealed class SaleAggregate
    {
        public CustomerKey Key { get; init; }

        /// <summary>The code as it was written on the sale, for a row whose customer no longer exists.
        /// <see cref="CustomerKey"/> upper-cases its copy so it can match; this one is for reading.</summary>
        public string DisplayCode { get; init; } = string.Empty;

        public string? Name { get; init; }
        public string Currency { get; init; } = string.Empty;
        public int SaleCount { get; init; }
        public decimal Gross { get; init; }
        public decimal Vat { get; init; }
        public decimal AmountPaid { get; init; }
    }

    /// <summary>
    /// The first and last trading day a customer has ever bought on, both as bare CAT days so the two
    /// sources are comparable.
    /// </summary>
    private readonly record struct SaleSpan(DateTime First, DateTime Last)
    {
        public SaleSpan Merge(SaleSpan other) => new(
            other.First < First ? other.First : First,
            other.Last > Last ? other.Last : Last);
    }

    /// <summary>
    /// One customer's row while it is still being assembled, with the parts that cannot go on the DTO:
    /// the per-currency subtotals waiting to be folded together, and every key its sales arrived under.
    /// A customer can have more than one — sales made before their record was deleted and re-created
    /// carry a different id from the ones made after.
    /// </summary>
    private sealed class RowAccumulator
    {
        public required RouteCustomerSalesRowDto Row { get; init; }
        public List<RouteCustomerSalesTotalsDto> Totals { get; } = [];
        public HashSet<CustomerKey> Keys { get; } = [];
    }

    public async Task<ErrorOr<RouteCustomerSalesSummaryDto>> Handle(
        GetRouteCustomerSalesSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var (from, to) = RouteCustomerSalesReporting.NormalizeWindow(query.From, query.To);
        var (fromUtc, toUtcExclusive) = RouteCustomerSalesReporting.ToUtcWindow(from, to);
        var dormantDays = query.DormantDays is > 0
            ? query.DormantDays.Value
            : RouteCustomerSalesReporting.DefaultDormantDays;

        var route = string.IsNullOrWhiteSpace(query.AssignedBusinessPartnerCode)
            ? null
            : query.AssignedBusinessPartnerCode.Trim();

        var customers = await LoadCustomersAsync(route, cancellationToken);

        var aggregates = await LoadOfflineAggregatesAsync(route, from, to, cancellationToken);
        aggregates.AddRange(await LoadOnlineAggregatesAsync(route, fromUtc, toUtcExclusive, cancellationToken));

        var lineCounts = await LoadLineCountsAsync(route, from, to, fromUtc, toUtcExclusive, cancellationToken);
        var openOrderCounts = await LoadOpenOrderCountsAsync(route, cancellationToken);
        var lifetimeSpans = await LoadLifetimeSpansAsync(route, cancellationToken);

        var rows = BuildRows(customers, aggregates, lineCounts, openOrderCounts, lifetimeSpans, query.IncludeInactive);

        return new RouteCustomerSalesSummaryDto
        {
            From = from,
            To = to,
            DormantDays = dormantDays,
            Routes = GroupByRoute(rows, dormantDays)
        };
    }

    private async Task<List<RouteCustomerEntity>> LoadCustomersAsync(string? route, CancellationToken cancellationToken)
    {
        var customersQuery = db.RouteCustomers.AsNoTracking();

        if (route is not null)
        {
            customersQuery = customersQuery.Where(customer => customer.AssignedBusinessPartnerCode == route);
        }

        return await customersQuery.ToListAsync(cancellationToken);
    }

    private async Task<List<SaleAggregate>> LoadOfflineAggregatesAsync(
        string? route,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var salesQuery = db.DesktopSales
            .AsNoTracking()
            // The online arm below already counts these, as the confirmed reservation they posted from.
            // Their row here exists only to carry the receipt the handset signed.
            .Where(sale => sale.SourceSystem != SaleSourceSystems.VanSalesOnline)
            .Where(sale => sale.RouteCustomerCode != null)
            .Where(sale => sale.DocDate >= from && sale.DocDate <= to);

        if (route is not null)
        {
            salesQuery = salesQuery.Where(sale => sale.CardCode == route);
        }

        var grouped = await salesQuery
            .GroupBy(sale => new
            {
                sale.RouteCustomerId,
                sale.RouteCustomerCode,
                sale.RouteCustomerName,
                sale.CardCode,
                sale.Currency
            })
            .Select(group => new
            {
                group.Key,
                SaleCount = group.Count(),
                Gross = group.Sum(sale => sale.TotalAmount),
                Vat = group.Sum(sale => sale.VatAmount),
                AmountPaid = group.Sum(sale => sale.AmountPaid)
            })
            .ToListAsync(cancellationToken);

        return grouped.Select(row => new SaleAggregate
        {
            Key = BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode),
            DisplayCode = row.Key.RouteCustomerCode ?? string.Empty,
            Name = row.Key.RouteCustomerName,
            Currency = RouteCustomerSalesReporting.NormalizeCurrency(row.Key.Currency),
            SaleCount = row.SaleCount,
            Gross = row.Gross,
            Vat = row.Vat,
            AmountPaid = row.AmountPaid
        }).ToList();
    }

    private async Task<List<SaleAggregate>> LoadOnlineAggregatesAsync(
        string? route,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var reservationsQuery = db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.RouteCustomerCode != null)
            .Where(reservation => reservation.Status == ReservationStatus.Confirmed)
            .Where(reservation => reservation.CreatedAt >= fromUtc && reservation.CreatedAt < toUtcExclusive);

        if (route is not null)
        {
            reservationsQuery = reservationsQuery.Where(reservation => reservation.CardCode == route);
        }

        var grouped = await reservationsQuery
            .GroupBy(reservation => new
            {
                reservation.RouteCustomerId,
                reservation.RouteCustomerCode,
                reservation.RouteCustomerName,
                reservation.CardCode,
                reservation.Currency
            })
            .Select(group => new
            {
                group.Key,
                SaleCount = group.Count(),
                Gross = group.Sum(reservation => reservation.TotalValue)
            })
            .ToListAsync(cancellationToken);

        return grouped.Select(row => new SaleAggregate
        {
            Key = BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode),
            DisplayCode = row.Key.RouteCustomerCode ?? string.Empty,
            Name = row.Key.RouteCustomerName,
            Currency = RouteCustomerSalesReporting.NormalizeCurrency(row.Key.Currency),
            SaleCount = row.SaleCount,
            Gross = row.Gross,
            // A reservation holds no tax split — that lives on the SAP invoice — so this reports no VAT
            // rather than reporting zero VAT as if it had been checked.
            Vat = 0m,
            AmountPaid = row.Gross
        }).ToList();
    }

    /// <summary>
    /// The first and last day each customer ever bought, read with no date filter at all.
    ///
    /// This is the query that separates a lapsed shop from a new one, and it cannot be windowed without
    /// becoming the thing it is here to fix: a customer whose last purchase predates the window is absent
    /// from every windowed aggregate, and absence there says nothing about whether they ever bought.
    ///
    /// Affordable because the database does the reducing. Both halves are grouped aggregates that return
    /// one row per customer, so this is the same shape as the windowed reads above with their date
    /// predicate removed — not a scan of history into memory.
    ///
    /// It can only date the rows the report already has. Sales belonging to a customer whose record was
    /// hard-deleted still get their own row, but only when they fall inside the window; a deleted
    /// customer with nothing recent has no row to attach a date to, which is unchanged behaviour.
    /// </summary>
    private async Task<Dictionary<CustomerKey, SaleSpan>> LoadLifetimeSpansAsync(
        string? route,
        CancellationToken cancellationToken)
    {
        var offlineQuery = db.DesktopSales
            .AsNoTracking()
            // Counted by the online arm below instead — see LoadOfflineAggregatesAsync.
            .Where(sale => sale.SourceSystem != SaleSourceSystems.VanSalesOnline)
            .Where(sale => sale.RouteCustomerCode != null);

        if (route is not null)
        {
            offlineQuery = offlineQuery.Where(sale => sale.CardCode == route);
        }

        var offline = await offlineQuery
            .GroupBy(sale => new { sale.RouteCustomerId, sale.RouteCustomerCode, sale.CardCode })
            .Select(group => new
            {
                group.Key,
                First = group.Min(sale => sale.DocDate),
                Last = group.Max(sale => sale.DocDate)
            })
            .ToListAsync(cancellationToken);

        var onlineQuery = db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.RouteCustomerCode != null)
            .Where(reservation => reservation.Status == ReservationStatus.Confirmed);

        if (route is not null)
        {
            onlineQuery = onlineQuery.Where(reservation => reservation.CardCode == route);
        }

        var online = await onlineQuery
            .GroupBy(reservation => new
            {
                reservation.RouteCustomerId,
                reservation.RouteCustomerCode,
                reservation.CardCode
            })
            .Select(group => new
            {
                group.Key,
                First = group.Min(reservation => reservation.CreatedAt),
                Last = group.Max(reservation => reservation.CreatedAt)
            })
            .ToListAsync(cancellationToken);

        var spans = new Dictionary<CustomerKey, SaleSpan>();

        foreach (var row in offline)
        {
            // DocDate is already a bare trading day in the van's own clock, so it needs no conversion.
            Fold(BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode),
                new SaleSpan(row.First.Date, row.Last.Date));
        }

        foreach (var row in online)
        {
            // A reservation is stamped in UTC, and the day it belongs to is the CAT day it was made on.
            Fold(BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode),
                new SaleSpan(
                    RouteCustomerSalesReporting.TradingDayOf(row.First),
                    RouteCustomerSalesReporting.TradingDayOf(row.Last)));
        }

        return spans;

        void Fold(CustomerKey key, SaleSpan span) =>
            spans[key] = spans.TryGetValue(key, out var existing) ? existing.Merge(span) : span;
    }

    /// <summary>
    /// Lines sold per customer, counted from the line tables directly.
    ///
    /// Counted here rather than as a sub-aggregate of the sales query above, because a per-group count
    /// over a navigation collection is the kind of thing that either becomes a correlated subquery per
    /// row or fails to translate at all. Two flat grouped counts are predictable.
    /// </summary>
    private async Task<Dictionary<CustomerKey, int>> LoadLineCountsAsync(
        string? route,
        DateTime from,
        DateTime to,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var offlineQuery = db.DesktopSaleLines
            .AsNoTracking()
            // Counted by the online arm below instead — see LoadOfflineAggregatesAsync.
            .Where(line => line.Sale.SourceSystem != SaleSourceSystems.VanSalesOnline)
            .Where(line => line.Sale.RouteCustomerCode != null)
            .Where(line => line.Sale.DocDate >= from && line.Sale.DocDate <= to);

        if (route is not null)
        {
            offlineQuery = offlineQuery.Where(line => line.Sale.CardCode == route);
        }

        var offline = await offlineQuery
            .GroupBy(line => new { line.Sale.RouteCustomerId, line.Sale.RouteCustomerCode, line.Sale.CardCode })
            .Select(group => new { group.Key, LineCount = group.Count() })
            .ToListAsync(cancellationToken);

        var onlineQuery = db.StockReservationLines
            .AsNoTracking()
            .Where(line => line.Reservation.RouteCustomerCode != null)
            .Where(line => line.Reservation.Status == ReservationStatus.Confirmed)
            .Where(line => line.Reservation.CreatedAt >= fromUtc && line.Reservation.CreatedAt < toUtcExclusive);

        if (route is not null)
        {
            onlineQuery = onlineQuery.Where(line => line.Reservation.CardCode == route);
        }

        var online = await onlineQuery
            .GroupBy(line => new
            {
                line.Reservation.RouteCustomerId,
                line.Reservation.RouteCustomerCode,
                line.Reservation.CardCode
            })
            .Select(group => new { group.Key, LineCount = group.Count() })
            .ToListAsync(cancellationToken);

        var counts = new Dictionary<CustomerKey, int>();

        foreach (var row in offline)
        {
            var key = BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode);
            counts[key] = counts.GetValueOrDefault(key) + row.LineCount;
        }

        foreach (var row in online)
        {
            var key = BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode);
            counts[key] = counts.GetValueOrDefault(key) + row.LineCount;
        }

        return counts;
    }

    /// <summary>
    /// Orders still waiting to become sales. Not windowed: an order placed before the window and still
    /// open is exactly the one someone needs to see, and hiding it behind a date filter would make the
    /// count answer a question nobody asked.
    /// </summary>
    private async Task<Dictionary<CustomerKey, int>> LoadOpenOrderCountsAsync(
        string? route,
        CancellationToken cancellationToken)
    {
        var ordersQuery = db.SalesOrders
            .AsNoTracking()
            .Where(order => order.RouteCustomerCode != null)
            .Where(order => order.InvoiceId == null && order.SAPDocNum == null)
            .Where(order => order.Status != SalesOrderStatus.Cancelled &&
                            order.Status != SalesOrderStatus.Rejected &&
                            order.Status != SalesOrderStatus.Fulfilled);

        if (route is not null)
        {
            ordersQuery = ordersQuery.Where(order => order.CardCode == route);
        }

        var grouped = await ordersQuery
            .GroupBy(order => new { order.RouteCustomerId, order.RouteCustomerCode, order.CardCode })
            .Select(group => new { group.Key, OrderCount = group.Count() })
            .ToListAsync(cancellationToken);

        return grouped.ToDictionary(
            row => BuildKey(row.Key.RouteCustomerId, row.Key.RouteCustomerCode, row.Key.CardCode),
            row => row.OrderCount);
    }

    private static List<RouteCustomerSalesRowDto> BuildRows(
        List<RouteCustomerEntity> customers,
        List<SaleAggregate> aggregates,
        Dictionary<CustomerKey, int> lineCounts,
        Dictionary<CustomerKey, int> openOrderCounts,
        Dictionary<CustomerKey, SaleSpan> lifetimeSpans,
        bool includeInactive)
    {
        var byId = customers
            .GroupBy(customer => customer.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var byCodeAndRoute = customers
            .GroupBy(customer => (Code: Normalize(customer.Code), Route: Normalize(customer.AssignedBusinessPartnerCode)))
            .ToDictionary(group => group.Key, group => group.First());

        // Every live customer gets an accumulator up front, including the ones with nothing to
        // accumulate — a customer who bought nothing is the whole point of the dormancy view, and they
        // appear in no sales table at all.
        var byCustomerId = customers.ToDictionary(
            customer => customer.Id,
            customer => new RowAccumulator { Row = NewRow(customer) });

        // Sales whose route customer has since been deleted, and cannot be matched to a live one. They
        // still happened and still belong in the route's takings, so they get their own row rather than
        // being silently dropped out of the total.
        var orphans = new Dictionary<CustomerKey, RowAccumulator>();

        foreach (var aggregate in aggregates)
        {
            var accumulator = Resolve(aggregate, byId, byCodeAndRoute, byCustomerId, orphans);
            var row = accumulator.Row;

            row.SaleCount += aggregate.SaleCount;

            accumulator.Totals.Add(new RouteCustomerSalesTotalsDto
            {
                Currency = aggregate.Currency,
                SaleCount = aggregate.SaleCount,
                Gross = aggregate.Gross,
                Vat = aggregate.Vat,
                AmountPaid = aggregate.AmountPaid
            });

            accumulator.Keys.Add(aggregate.Key);
        }

        var accumulators = byCustomerId.Values.Concat(orphans.Values).ToList();

        foreach (var accumulator in accumulators)
        {
            var row = accumulator.Row;

            // A live customer's lines and open orders can sit under keys no sale aggregate produced —
            // an order placed but never invoiced has no sale to be found through, and a sale written
            // before the FK was nulled carries the id while a later one carries only the code.
            if (row.RouteCustomerId is { } customerId)
            {
                var route = Normalize(row.AssignedBusinessPartnerCode);
                accumulator.Keys.Add(new CustomerKey(customerId, Normalize(row.Code), route));
                accumulator.Keys.Add(new CustomerKey(null, Normalize(row.Code), route));
            }

            // All-time, over every key the row answers to, and set here rather than from the aggregates
            // above because the window's own last sale cannot tell a lapsed shop from a new one. A null
            // that survives this has one meaning left: the customer has never bought.
            SaleSpan? lifetime = null;

            foreach (var key in accumulator.Keys)
            {
                if (lifetimeSpans.TryGetValue(key, out var span))
                {
                    lifetime = lifetime is null ? span : lifetime.Value.Merge(span);
                }
            }

            row.FirstSaleAt = lifetime?.First;
            row.LastSaleAt = lifetime?.Last;

            row.TotalsByCurrency = RouteCustomerSalesReporting.SumByCurrency(accumulator.Totals);
            row.DaysSinceLastSale = RouteCustomerSalesReporting.DaysSinceLastSale(row.LastSaleAt);
            row.LineCount = accumulator.Keys.Sum(key => lineCounts.GetValueOrDefault(key));
            row.OpenOrderCount = accumulator.Keys.Sum(key => openOrderCounts.GetValueOrDefault(key));
        }

        // An inactive customer who traded inside the window is never hidden: the filter is about which
        // dormant customers are worth chasing, not about which takings count.
        return accumulators
            .Select(accumulator => accumulator.Row)
            .Where(row => includeInactive || row.IsActive || row.SaleCount > 0)
            .OrderBy(row => row.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Finds the row an aggregate belongs to: by customer id first, then by code and route for sales
    /// whose customer was deleted and re-created, and finally a row of its own when neither matches.
    /// </summary>
    private static RowAccumulator Resolve(
        SaleAggregate aggregate,
        Dictionary<int, RouteCustomerEntity> byId,
        Dictionary<(string Code, string Route), RouteCustomerEntity> byCodeAndRoute,
        Dictionary<int, RowAccumulator> byCustomerId,
        Dictionary<CustomerKey, RowAccumulator> orphans)
    {
        RouteCustomerEntity? customer = null;

        if (aggregate.Key.RouteCustomerId is { } id && byId.TryGetValue(id, out var byIdMatch))
        {
            customer = byIdMatch;
        }
        else if (byCodeAndRoute.TryGetValue((aggregate.Key.Code, aggregate.Key.RouteCode), out var byCodeMatch))
        {
            customer = byCodeMatch;
        }

        if (customer is not null && byCustomerId.TryGetValue(customer.Id, out var matched))
        {
            return matched;
        }

        if (!orphans.TryGetValue(aggregate.Key, out var orphan))
        {
            orphan = new RowAccumulator
            {
                Row = new RouteCustomerSalesRowDto
                {
                    RouteCustomerId = null,
                    Code = aggregate.DisplayCode,
                    Name = string.IsNullOrWhiteSpace(aggregate.Name) ? aggregate.DisplayCode : aggregate.Name,
                    AssignedBusinessPartnerCode = aggregate.Key.RouteCode,
                    IsActive = false,
                    IsDeleted = true
                }
            };
            orphans[aggregate.Key] = orphan;
        }

        return orphan;
    }

    private static List<RouteSalesGroupDto> GroupByRoute(List<RouteCustomerSalesRowDto> rows, int dormantDays) =>
        rows
            .GroupBy(row => row.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouteSalesGroupDto
            {
                AssignedBusinessPartnerCode = group.Key,
                CustomerCount = group.Count(),
                SaleCount = group.Sum(row => row.SaleCount),
                // Both read the all-time last sale, and they are disjoint by construction: a customer
                // either has one or does not. Counting the never-bought off the window's sale count is
                // what used to file every lapsed shop under "never converted".
                NeverBoughtCount = group.Count(row => row.LastSaleAt is null),
                DormantCount = group.Count(row => row.DaysSinceLastSale is { } days && days > dormantDays),
                TotalsByCurrency = RouteCustomerSalesReporting.SumByCurrency(
                    group.SelectMany(row => row.TotalsByCurrency)),
                Customers = group.ToList()
            })
            .OrderBy(group => group.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static RouteCustomerSalesRowDto NewRow(RouteCustomerEntity customer) => new()
    {
        RouteCustomerId = customer.Id,
        Code = customer.Code,
        Name = customer.Name,
        AssignedBusinessPartnerCode = customer.AssignedBusinessPartnerCode,
        IsActive = customer.IsActive,
        IsDeleted = false,
        CapturedAt = customer.CreatedAt,
        Phone = customer.Phone
    };

    private static CustomerKey BuildKey(int? routeCustomerId, string? code, string? routeCode) =>
        new(routeCustomerId, Normalize(code), Normalize(routeCode));

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
