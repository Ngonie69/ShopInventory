using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

/// <summary>
/// Builds the ADR performance report: each ADR's orders and sales, and the ADRs' share of every van's.
/// </summary>
/// <remarks>
/// Sales read through <see cref="VanSalesFactReader"/>, so a rep's takings here are the same figure
/// the performance and compliance reports show.
///
/// A van sales order is a mobile order captured by a van rep — an <c>ADR</c> or <c>Sales</c> account.
/// That is the population the "all vans" side of every share is measured against. It is decided by
/// who raised the order rather than by its comment text, so it holds for orders captured before the
/// van app started writing one.
/// </remarks>
public sealed class GetAdrPerformanceReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetAdrPerformanceReportQuery, ErrorOr<AdrPerformanceReportResult>>
{
    private static readonly string[] VanRepRoles = [ApplicationRoles.Adr, ApplicationRoles.Sales];

    /// <summary>
    /// The most orders one ADR's detail lists. Every figure still counts the rest.
    /// </summary>
    public const int MaximumOrdersListed = 500;

    internal const string AdrChannel = "ADRs";

    public async Task<ErrorOr<AdrPerformanceReportResult>> Handle(
        GetAdrPerformanceReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation(
                "VanSalesReports.InvalidRange",
                "The end of the period cannot be before its start.");
        }

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var vanReps = await db.Users
            .AsNoTracking()
            .Where(user => VanRepRoles.Contains(user.Role))
            .Select(user => new VanRep(
                user.Id,
                user.Username,
                user.FirstName,
                user.LastName,
                user.Role,
                user.AssignedBusinessPartnerCode,
                user.IsActive))
            .ToListAsync(cancellationToken);

        var vanRepIds = vanReps.Select(rep => rep.Id).ToList();
        var adrIds = vanReps
            .Where(rep => string.Equals(rep.Role, ApplicationRoles.Adr, StringComparison.OrdinalIgnoreCase))
            .Select(rep => rep.Id)
            .ToHashSet();

        var orders = await LoadOrdersAsync(from, to, vanRepIds, cancellationToken);

        // Every van's sales, not only the ADRs': the shares need the whole.
        var sales = await VanSalesFactReader.LoadSalesAsync(
            db,
            new VanSalesFactFilter(from, to),
            cancellationToken);

        var adrOrders = orders.Where(order => adrIds.Contains(order.UserId)).ToList();
        var adrSales = sales.Where(sale => adrIds.Contains(sale.UserId)).ToList();
        var visits = await LoadVisitsAsync(adrIds, from, to, cancellationToken);

        var visitsByAdr = visits
            .GroupBy(visit => visit.Key.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(day => day.Value.Count));

        var overall = new AdrPerformanceOverallResult(
            AdrCount: vanReps.Count(rep => adrIds.Contains(rep.Id) && rep.IsActive),
            ActiveAdrCount: adrOrders.Select(order => order.UserId)
                .Concat(adrSales.Select(sale => sale.UserId))
                .Distinct()
                .Count(),
            OrderCounts: CountOrders(adrOrders),
            Orders: Contribution(
                OrderMoney(adrOrders),
                OrderMoney(orders)),
            Sales: Contribution(
                VanSalesMeasures.MoneyByCurrency(adrSales),
                VanSalesMeasures.MoneyByCurrency(sales)),
            Calls: visits.Count == 0 ? null : visits.Values.Sum(shops => shops.Count),
            ProductiveCalls: CountProductiveCalls(adrOrders, adrSales));

        var vanOrderTotals = OrderMoney(orders);
        var vanSaleTotals = VanSalesMeasures.MoneyByCurrency(sales);

        var ordersByAdr = adrOrders.ToLookup(order => order.UserId);
        var salesByAdr = adrSales.ToLookup(sale => sale.UserId);

        // An inactive ADR is listed only when the period holds something of theirs: a rep who left
        // last year is not a row of zeroes in this month's league table, but their last month is.
        var rows = vanReps
            .Where(rep => adrIds.Contains(rep.Id))
            .Where(rep => query.UserId is null || rep.Id == query.UserId)
            .Where(rep => rep.IsActive || ordersByAdr[rep.Id].Any() || salesByAdr[rep.Id].Any())
            .Select(rep => BuildRow(rep, ordersByAdr[rep.Id].ToList(), salesByAdr[rep.Id].ToList(),
                vanOrderTotals, vanSaleTotals, visitsByAdr.TryGetValue(rep.Id, out var calls) ? calls : null))
            .OrderByDescending(row => row.ActiveDays > 0)
            .ThenByDescending(row => LeadGross(row.SalesTotalsByCurrency, vanSaleTotals))
            .ThenByDescending(row => LeadGross(row.OrderTotalsByCurrency, vanOrderTotals))
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var channels = await BuildChannelsAsync(from, to, adrIds, cancellationToken);

        var detail = query.UserId is { } userId && adrIds.Contains(userId)
            ? await BuildDetailAsync(
                userId,
                ordersByAdr[userId].ToList(),
                salesByAdr[userId].ToList(),
                query.TopItems,
                cancellationToken)
            : null;

        var scoped = query.UserId is { } one ? adrOrders.Where(order => order.UserId == one).ToList() : adrOrders;

        return new AdrPerformanceReportResult(
            from,
            to,
            overall,
            rows,
            channels,
            detail,
            BuildCaveats(scoped, rows, query.UserId));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every van rep's mobile sales orders in the period, by the CAT day they were captured.
    /// </summary>
    /// <remarks>
    /// <c>OrderDate</c> is a UTC instant, so the window is converted rather than compared as dates —
    /// otherwise every order after 22:00 CAT lands on the day before.
    /// </remarks>
    private async Task<List<VanOrderFact>> LoadOrdersAsync(
        DateTime from,
        DateTime to,
        List<Guid> vanRepIds,
        CancellationToken cancellationToken)
    {
        if (vanRepIds.Count == 0)
        {
            return [];
        }

        var (fromUtc, toUtcExclusive) = VanSalesFacts.ToUtcWindow(from, to);

        var rows = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Source == SalesOrderSource.Mobile
                            && order.CreatedByUserId != null
                            && vanRepIds.Contains(order.CreatedByUserId.Value)
                            && order.OrderDate >= fromUtc
                            && order.OrderDate < toUtcExclusive)
            .Select(order => new
            {
                order.Id,
                order.OrderNumber,
                UserId = order.CreatedByUserId!.Value,
                order.OrderDate,
                order.Status,
                order.SAPDocNum,
                order.Currency,
                order.DocTotal,
                order.RouteCustomerCode,
                order.RouteCustomerName,
                LineCount = order.Lines.Count
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new VanOrderFact(
                row.UserId,
                VanSalesFacts.TradingDayOf(row.OrderDate),
                row.Status,
                row.SAPDocNum is > 0,
                RouteCustomerSalesReporting.NormalizeCurrency(row.Currency),
                row.DocTotal,
                string.IsNullOrWhiteSpace(row.RouteCustomerCode) ? null : row.RouteCustomerCode.Trim())
            {
                Id = row.Id,
                OrderNumber = row.OrderNumber,
                SapDocNum = row.SAPDocNum is > 0 ? row.SAPDocNum : null,
                CustomerName = string.IsNullOrWhiteSpace(row.RouteCustomerName) ? null : row.RouteCustomerName.Trim(),
                LineCount = row.LineCount
            })
            .ToList();
    }

    /// <summary>
    /// The shops each ADR checked in at, per trading day. ADRs work the van app, so their calls are on
    /// the van sales channel; a shop checked in at twice in a day is one call.
    /// </summary>
    private async Task<Dictionary<VanSalesDayKey, HashSet<string>>> LoadVisitsAsync(
        HashSet<Guid> adrIds,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        if (adrIds.Count == 0)
        {
            return [];
        }

        var ids = adrIds.ToList();
        var (fromUtc, toUtcExclusive) = VanSalesFacts.ToUtcWindow(from, to);

        var entries = await db.TimesheetEntries
            .AsNoTracking()
            .Where(entry => entry.Channel == TimesheetChannel.VanSales
                            && ids.Contains(entry.UserId)
                            && entry.CheckInTime >= fromUtc
                            && entry.CheckInTime < toUtcExclusive)
            .Select(entry => new { entry.UserId, entry.CheckInTime, entry.CustomerCode })
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(entry => new VanSalesDayKey(entry.UserId, VanSalesFacts.TradingDayOf(entry.CheckInTime)))
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.CustomerCode.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every sales order raised in the period, from any source, split by who raised it.
    /// </summary>
    /// <remarks>
    /// Dated by <c>OrderDate</c>, the instant the van side above uses, so the ADR row here and the
    /// ADR order value above cover the same orders whenever the ADRs order only from the van app.
    /// </remarks>
    private async Task<List<AdrChannelResult>> BuildChannelsAsync(
        DateTime from,
        DateTime to,
        HashSet<Guid> adrIds,
        CancellationToken cancellationToken)
    {
        var (fromUtc, toUtcExclusive) = VanSalesFacts.ToUtcWindow(from, to);

        var orders = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.OrderDate >= fromUtc
                            && order.OrderDate < toUtcExclusive
                            && order.Status != SalesOrderStatus.Cancelled
                            && order.Status != SalesOrderStatus.Rejected)
            .Select(order => new { order.CreatedByUserId, order.Source, order.Currency, order.DocTotal })
            .ToListAsync(cancellationToken);

        var others = orders
            .Where(order => order.CreatedByUserId is { } id && !adrIds.Contains(id))
            .Select(order => order.CreatedByUserId!.Value)
            .Distinct()
            .ToList();

        var roles = others.Count == 0
            ? []
            : await db.Users
                .AsNoTracking()
                .Where(user => others.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.Role, cancellationToken);

        string ChannelOf(Guid? creator, SalesOrderSource source)
        {
            if (creator is { } id && adrIds.Contains(id))
            {
                return AdrChannel;
            }

            if (source == SalesOrderSource.VanSalesCustomer)
            {
                return "Customer ordering app";
            }

            if (creator is not { } staff || !roles.TryGetValue(staff, out var role))
            {
                return "Not attributed";
            }

            return role.Trim().ToUpperInvariant() switch
            {
                "SALES" => "Van sales reps",
                "MERCHANDISER" => "Merchandisers",
                "SALESREP" => "Sales reps",
                _ => "Office and other staff"
            };
        }

        var whole = orders
            .GroupBy(order => RouteCustomerSalesReporting.NormalizeCurrency(order.Currency), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(order => order.DocTotal), StringComparer.OrdinalIgnoreCase);

        var channels = orders
            .GroupBy(order => ChannelOf(order.CreatedByUserId, order.Source))
            .Select(group => new AdrChannelResult(
                Channel: group.Key,
                IsAdr: group.Key == AdrChannel,
                OrderCount: group.Count(),
                PeopleCount: group
                    .Where(order => order.CreatedByUserId.HasValue)
                    .Select(order => order.CreatedByUserId)
                    .Distinct()
                    .Count(),
                Shares: group
                    .GroupBy(order => RouteCustomerSalesReporting.NormalizeCurrency(order.Currency), StringComparer.OrdinalIgnoreCase)
                    .Select(currency =>
                    {
                        var gross = currency.Sum(order => order.DocTotal);
                        var total = whole[currency.Key];
                        return new AdrChannelShareResult(currency.Key, gross, total > 0 ? (double)(gross / total) : null);
                    })
                    .OrderByDescending(share => share.Gross)
                    .ThenBy(share => share.Currency, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        // Listed even with nothing raised, so "0%" reads against a whole rather than leaving the
        // reader to wonder whether the ADRs were counted at all.
        if (channels.All(channel => !channel.IsAdr))
        {
            channels.Add(new AdrChannelResult(AdrChannel, true, 0, 0, []));
        }

        var lead = whole.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();

        decimal LeadGrossOf(AdrChannelResult channel) =>
            channel.Shares.FirstOrDefault(share =>
                string.Equals(share.Currency, lead, StringComparison.OrdinalIgnoreCase))?.Gross ?? 0m;

        return channels
            .OrderByDescending(LeadGrossOf)
            .ThenByDescending(channel => channel.OrderCount)
            .ThenBy(channel => channel.Channel, StringComparer.Ordinal)
            .ToList();
    }

    // ── One ADR ─────────────────────────────────────────────────────────────────

    private async Task<AdrPerformanceDetailResult> BuildDetailAsync(
        Guid userId,
        List<VanOrderFact> orders,
        List<VanSaleFact> sales,
        int topItems,
        CancellationToken cancellationToken)
    {
        var live = orders.Where(order => !order.IsWithdrawn).ToList();

        var shopKeys = live.Where(order => order.Customer is not null).Select(order => order.Customer!.ToUpperInvariant())
            .Concat(sales.Where(sale => sale.RouteCustomerCode is not null).Select(sale => sale.RouteCustomerCode!.ToUpperInvariant()))
            .Distinct();

        var shops = shopKeys
            .Select(key =>
            {
                var shopOrders = live.Where(order => order.Customer?.ToUpperInvariant() == key).ToList();
                var shopSales = sales.Where(sale => sale.RouteCustomerCode?.ToUpperInvariant() == key).ToList();

                return new AdrShopResult(
                    CustomerCode: shopOrders.Select(order => order.Customer).FirstOrDefault()
                                  ?? shopSales.Select(sale => sale.RouteCustomerCode).First()!,
                    CustomerName: shopOrders.Select(order => order.CustomerName)
                        .Concat(shopSales.Select(sale => sale.RouteCustomerName))
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                    OrderCount: shopOrders.Count,
                    OrderTotalsByCurrency: OrderMoney(shopOrders),
                    SaleCount: shopSales.Count,
                    SalesTotalsByCurrency: VanSalesMeasures.MoneyByCurrency(shopSales),
                    LastActiveOn: shopOrders.Select(order => order.TradingDate)
                        .Concat(shopSales.Select(sale => sale.TradingDate))
                        .Max());
            })
            .OrderByDescending(shop => shop.OrderCount + shop.SaleCount)
            .ThenByDescending(shop => shop.LastActiveOn)
            .ThenBy(shop => shop.CustomerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = await BuildItemsAsync(live, topItems, cancellationToken);

        var listed = orders
            .OrderByDescending(order => order.TradingDate)
            .ThenByDescending(order => order.Id)
            .Take(MaximumOrdersListed)
            .Select(order => new AdrOrderResult(
                Id: order.Id,
                OrderNumber: order.OrderNumber,
                SapDocNum: order.SapDocNum,
                TradingDate: order.TradingDate,
                CustomerCode: order.Customer,
                CustomerName: order.CustomerName,
                Status: order.Status.ToString(),
                Stage: order.IsWithdrawn ? "Cancelled"
                    : order.IsFulfilled ? "Fulfilled"
                    : order.InSap ? "In SAP"
                    : "Not yet in SAP",
                Currency: order.Currency,
                DocTotal: order.DocTotal,
                LineCount: order.LineCount))
            .ToList();

        return new AdrPerformanceDetailResult(
            UserId: userId,
            Shops: shops,
            Items: items,
            Orders: listed,
            OrdersNotListed: Math.Max(orders.Count - MaximumOrdersListed, 0));
    }

    /// <summary>
    /// What one ADR ordered, ranked on reach as the van performance report ranks items. Lines are read
    /// only for this one ADR's orders, in chunks so a long period cannot build an IN list Postgres
    /// refuses.
    /// </summary>
    private async Task<List<AdrItemResult>> BuildItemsAsync(
        List<VanOrderFact> live,
        int topItems,
        CancellationToken cancellationToken)
    {
        var byId = live.ToDictionary(order => order.Id);
        var lines = new List<(int OrderId, string ItemCode, string? Description, decimal Quantity, string? UoM, decimal LineTotal)>();

        foreach (var chunk in byId.Keys.Chunk(2000))
        {
            var rows = await db.SalesOrderLines
                .AsNoTracking()
                .Where(line => chunk.Contains(line.SalesOrderId))
                .Select(line => new { line.SalesOrderId, line.ItemCode, line.ItemDescription, line.Quantity, line.UoMCode, line.LineTotal })
                .ToListAsync(cancellationToken);

            lines.AddRange(rows.Select(row => (row.SalesOrderId, row.ItemCode.Trim().ToUpperInvariant(),
                row.ItemDescription, row.Quantity, string.IsNullOrWhiteSpace(row.UoMCode) ? null : row.UoMCode.Trim(),
                row.LineTotal)));
        }

        var ranked = lines
            .GroupBy(line => line.ItemCode)
            .Select(group => new
            {
                ItemCode = group.Key,
                Description = group.Select(line => line.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                LineCount = group.Count(),
                ShopCount = group
                    .Select(line => byId[line.OrderId].Customer?.ToUpperInvariant() ?? VanSalesMeasures.Unattributed)
                    .Distinct()
                    .Count(),
                Quantities = group
                    .GroupBy(line => line.UoM)
                    .Select(unit => new VanSalesQuantityResult(unit.Key, unit.Sum(line => line.Quantity), unit.Count()))
                    .OrderByDescending(quantity => quantity.LineCount)
                    .ThenBy(quantity => quantity.UoMCode, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Totals = group
                    .GroupBy(line => byId[line.OrderId].Currency, StringComparer.OrdinalIgnoreCase)
                    .Select(currency => new VanSalesLineMoneyResult(currency.Key, currency.Count(), currency.Sum(line => line.LineTotal)))
                    .OrderByDescending(total => total.Gross)
                    .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderByDescending(item => item.LineCount)
            .ThenByDescending(item => item.ShopCount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (topItems > 0 ? ranked.Take(topItems) : ranked)
            .Select((item, index) => new AdrItemResult(
                index + 1, item.ItemCode, item.Description, item.LineCount, item.ShopCount, item.Quantities, item.Totals))
            .ToList();
    }

    // ── Caveats ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the figures cannot see, said on the report rather than left to be discovered. Empty when
    /// there is nothing to say, so its presence is itself the signal.
    /// </summary>
    private static List<string> BuildCaveats(
        List<VanOrderFact> orders,
        List<AdrPerformanceRepResult> rows,
        Guid? userId)
    {
        var caveats = new List<string>();

        var inSapNotFulfilled = orders.Count(order => order.InSap && !order.IsFulfilled && !order.IsWithdrawn);
        if (inSapNotFulfilled > 0)
        {
            caveats.Add(
                $"{inSapNotFulfilled:N0} order{Plural(inSapNotFulfilled)} reached SAP but {(inSapNotFulfilled == 1 ? "is" : "are")} not marked fulfilled here. "
                + "Fulfilled counts only orders turned into an invoice through this system; an order invoiced "
                + "directly in SAP B1 still reads as in SAP.");
        }

        var unpriced = orders.Count(order => !order.IsWithdrawn && order.DocTotal == 0m);
        if (unpriced > 0)
        {
            caveats.Add(
                $"{unpriced:N0} order{Plural(unpriced)} carr{(unpriced == 1 ? "ies" : "y")} a zero total — captured without prices and not priced yet. "
                + "They count as orders but add nothing to value.");
        }

        var scopedRows = userId is { } one ? rows.Where(row => row.UserId == one).ToList() : rows;
        var unmeasured = scopedRows.Count(row => row.Calls is null && row.ActiveDays > 0);
        if (unmeasured > 0)
        {
            caveats.Add(
                userId.HasValue
                    ? "This ADR recorded no visits on the van app, so there is no strike rate to show."
                    : $"{unmeasured:N0} ADR{Plural(unmeasured)} ordered or sold but recorded no visits on the van app, so "
                      + $"{(unmeasured == 1 ? "has" : "have")} no strike rate. The ADRs' strike rate counts their orders and not their calls.");
        }

        return caveats;
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    /// <summary>A shop that ordered or bought on a day, once however many documents it gave.</summary>
    private static int CountProductiveCalls(IEnumerable<VanOrderFact> orders, IEnumerable<VanSaleFact> sales) =>
        orders
            .Where(order => !order.IsWithdrawn && order.Customer is not null)
            .Select(order => (order.UserId, order.TradingDate, Shop: order.Customer!.ToUpperInvariant()))
            .Concat(sales
                .Where(sale => sale.RouteCustomerCode is not null)
                .Select(sale => (sale.UserId, sale.TradingDate, Shop: sale.RouteCustomerCode!.ToUpperInvariant())))
            .Distinct()
            .Count();

    // ── Building ────────────────────────────────────────────────────────────────

    private static AdrPerformanceRepResult BuildRow(
        VanRep rep,
        List<VanOrderFact> orders,
        List<VanSaleFact> sales,
        List<VanSalesMoneyResult> vanOrderTotals,
        List<VanSalesMoneyResult> vanSaleTotals,
        int? calls)
    {
        var orderTotals = OrderMoney(orders);
        var saleTotals = VanSalesMeasures.MoneyByCurrency(sales);
        var fullName = $"{rep.FirstName} {rep.LastName}".Trim();

        var currencies = vanOrderTotals.Select(total => total.Currency)
            .Concat(vanSaleTotals.Select(total => total.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var shares = currencies
            .Select(currency => new AdrShareResult(
                currency,
                Share(orderTotals, vanOrderTotals, currency),
                Share(saleTotals, vanSaleTotals, currency)))
            .Where(share => share.OrderShare is not null || share.SalesShare is not null)
            .ToList();

        return new AdrPerformanceRepResult(
            UserId: rep.Id,
            Username: rep.Username,
            FullName: string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            VanAccountCode: string.IsNullOrWhiteSpace(rep.AccountCode) ? null : rep.AccountCode.Trim(),
            IsActive: rep.IsActive,
            ActiveDays: orders.Select(order => order.TradingDate)
                .Concat(sales.Select(sale => sale.TradingDate))
                .Distinct()
                .Count(),
            OrderCounts: CountOrders(orders),
            OrderCustomerCount: orders
                .Where(order => order.Customer is not null)
                .Select(order => order.Customer!.ToUpperInvariant())
                .Distinct()
                .Count(),
            OrderTotalsByCurrency: orderTotals,
            SaleCustomerCount: sales
                .Where(sale => sale.RouteCustomerCode is not null)
                .Select(sale => sale.RouteCustomerCode!.ToUpperInvariant())
                .Distinct()
                .Count(),
            SalesTotalsByCurrency: saleTotals,
            Shares: shares,
            Calls: calls,
            ProductiveCalls: CountProductiveCalls(orders, sales));
    }

    /// <summary>
    /// Cancelled and rejected orders are counted but carry no money: an order that will never be
    /// delivered is not a contribution.
    /// </summary>
    private static List<VanSalesMoneyResult> OrderMoney(IEnumerable<VanOrderFact> orders) =>
        orders
            .Where(order => !order.IsWithdrawn)
            .GroupBy(order => order.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesMoneyResult(
                Currency: group.Key,
                DocumentCount: group.Count(),
                DropCount: group
                    .Select(order => (order.UserId, order.TradingDate,
                        order.Customer?.ToUpperInvariant() ?? VanSalesMeasures.Unattributed))
                    .Distinct()
                    .Count(),
                Gross: group.Sum(order => order.DocTotal)))
            .OrderByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static AdrOrderCountsResult CountOrders(IReadOnlyCollection<VanOrderFact> orders) =>
        new(
            Total: orders.Count,
            InSap: orders.Count(order => order.InSap),
            Fulfilled: orders.Count(order => order.IsFulfilled),
            Pending: orders.Count(order => !order.InSap && !order.IsWithdrawn),
            Cancelled: orders.Count(order => order.IsWithdrawn));

    private static List<AdrContributionResult> Contribution(
        List<VanSalesMoneyResult> adr,
        List<VanSalesMoneyResult> all) =>
        all
            .Select(van =>
            {
                var mine = adr.FirstOrDefault(total =>
                    string.Equals(total.Currency, van.Currency, StringComparison.OrdinalIgnoreCase));

                return new AdrContributionResult(
                    van.Currency,
                    mine?.DocumentCount ?? 0,
                    mine?.Gross ?? 0m,
                    van.DocumentCount,
                    van.Gross);
            })
            .ToList();

    private static double? Share(
        List<VanSalesMoneyResult> mine,
        List<VanSalesMoneyResult> all,
        string currency)
    {
        var whole = all.FirstOrDefault(total =>
            string.Equals(total.Currency, currency, StringComparison.OrdinalIgnoreCase));

        if (whole is null || whole.Gross <= 0)
        {
            return null;
        }

        var part = mine.FirstOrDefault(total =>
            string.Equals(total.Currency, currency, StringComparison.OrdinalIgnoreCase));

        return (double)((part?.Gross ?? 0m) / whole.Gross);
    }

    /// <summary>
    /// What a row ranks on: its takings in the currency the vans took most in, so a ZiG-heavy rep
    /// is not ranked by adding ZiG to USD.
    /// </summary>
    private static decimal LeadGross(List<VanSalesMoneyResult> mine, List<VanSalesMoneyResult> all)
    {
        if (all.Count == 0)
        {
            return 0m;
        }

        return mine.FirstOrDefault(total =>
            string.Equals(total.Currency, all[0].Currency, StringComparison.OrdinalIgnoreCase))?.Gross ?? 0m;
    }

    private sealed record VanRep(
        Guid Id,
        string Username,
        string? FirstName,
        string? LastName,
        string Role,
        string? AccountCode,
        bool IsActive);

    private sealed record VanOrderFact(
        Guid UserId,
        DateTime TradingDate,
        SalesOrderStatus Status,
        bool InSap,
        string Currency,
        decimal DocTotal,
        string? Customer)
    {
        public bool IsWithdrawn => Status is SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected;

        public bool IsFulfilled => Status is SalesOrderStatus.Fulfilled or SalesOrderStatus.PartiallyFulfilled;

        public int Id { get; init; }

        public string OrderNumber { get; init; } = string.Empty;

        public int? SapDocNum { get; init; }

        public string? CustomerName { get; init; }

        public int LineCount { get; init; }
    }
}
