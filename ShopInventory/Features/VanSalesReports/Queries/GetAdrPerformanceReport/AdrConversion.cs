using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Features.VanSalesAttendance;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using static ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport.GetAdrPerformanceReportHandler;

namespace ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

/// <summary>
/// Follows each ADR order to the van sale it became, and measures what was actually sold.
/// </summary>
/// <remarks>
/// <para><b>The link.</b> A van turns an order into an invoice through
/// <c>ConvertSalesOrderToInvoiceHandler</c>, which reserves the stock under the van's own reference and
/// queues the invoice with the order's id on it (<see cref="InvoiceQueueEntity.SalesOrderId"/>). That
/// queue entry is the one record holding both ends, so an order is invoiced when a live queue entry names
/// it and the sale filed under that entry's reference is in <see cref="VanSalesFactReader"/>. An order
/// invoiced straight in SAP B1 leaves no such entry, and reads as having no linked invoice.</para>
/// <para><b>Short supply.</b> The van may send its own lines when it converts — fewer units, or an item
/// left off — and that is where most of the difference between an order and its sale comes from. Van
/// sale lines carry no unit, so the two are compared item by item. A shortfall is valued at the order's
/// own prices, scaled from the net lines to the order's VAT-inclusive total.</para>
/// <para>Loading (<see cref="LoadAsync"/>) and measuring (<see cref="Build"/>) are apart so the margin
/// can be read once over every sale the report needs, and so the measuring can be tested without a
/// database.</para>
/// </remarks>
internal static class AdrConversion
{
    /// <summary>Below this a difference is rounding, not a shortfall or a reprice.</summary>
    private const decimal Tolerance = 0.005m;

    internal sealed record Link(string Reference, int? InvoiceDocNum);

    internal sealed record OrderLine(string ItemCode, string? Description, decimal Quantity, string? UoMCode, decimal LineTotal);

    /// <summary>Everything read from the database for one period's orders.</summary>
    internal sealed record Evidence(
        IReadOnlyDictionary<int, List<Link>> LinksByOrder,
        IReadOnlyDictionary<string, List<VanSaleFact>> SalesByReference,
        IReadOnlyDictionary<string, List<VanSaleLineFact>> LinesByReference,
        IReadOnlyDictionary<int, List<OrderLine>> OrderLines)
    {
        public static readonly Evidence None = new(
            new Dictionary<int, List<Link>>(),
            new Dictionary<string, List<VanSaleFact>>(),
            new Dictionary<string, List<VanSaleLineFact>>(),
            new Dictionary<int, List<OrderLine>>());

        public IEnumerable<VanSaleFact> Sales => SalesByReference.Values.SelectMany(sales => sales);
    }

    /// <summary>What the measuring needs besides the evidence.</summary>
    /// <param name="Orders">Every ADR's orders in the period, cancelled ones included.</param>
    /// <param name="PeriodSales">Every van sale made in the period, for the share of the vans' takings.</param>
    /// <param name="AdrOwnSales">Sales the ADRs made themselves, which count towards a productive call.</param>
    /// <param name="Rows">The ADRs to give a row, in the order the report lists them.</param>
    /// <param name="Detail">The ADRs whose days, shops, items and orders to list; empty for none.</param>
    internal sealed record Input(
        IReadOnlyList<VanOrderFact> Orders,
        IReadOnlyList<VanSaleFact> PeriodSales,
        IReadOnlyList<VanSaleFact> AdrOwnSales,
        IReadOnlyDictionary<VanSalesDayKey, HashSet<string>> Visits,
        IReadOnlyDictionary<string, string> VisitNames,
        IReadOnlyDictionary<Guid, string?> AdrAccounts,
        IReadOnlyDictionary<Guid, AdrAssignedRoutes.AssignedRoute> Routes,
        IReadOnlyList<Guid> Rows,
        IReadOnlySet<Guid> Detail,
        DateTime TodayCat);

    internal sealed record Analysis(
        AdrConversionSummaryResult Overall,
        List<AdrConversionRowResult> Rows,
        List<AdrConversionDayResult> Days,
        List<AdrConversionShopResult> Shops,
        List<AdrConversionItemResult> Items,
        List<AdrLedgerOrderResult> Orders,
        int UnpricedInvoiced,
        int StaleWithoutInvoice,
        int RecentAwaitingInvoice);

    // ── Loading ─────────────────────────────────────────────────────────────────

    public static async Task<Evidence> LoadAsync(
        ApplicationDbContext db,
        TaxSettings tax,
        IReadOnlyList<VanOrderFact> orders,
        CancellationToken cancellationToken)
    {
        var liveIds = orders.Where(order => !order.IsWithdrawn).Select(order => order.Id).ToList();
        if (liveIds.Count == 0)
        {
            return Evidence.None;
        }

        var links = new List<(int OrderId, string Reference, int? DocNum)>();
        var orderLines = new List<(int OrderId, OrderLine Line)>();

        // Chunked so a long period cannot build a parameter list Postgres refuses.
        foreach (var chunk in liveIds.Chunk(2000))
        {
            var queued = await db.InvoiceQueue
                .AsNoTracking()
                .Where(entry => entry.SalesOrderId != null
                                && chunk.Contains(entry.SalesOrderId.Value)
                                && entry.Status != InvoiceQueueStatus.Cancelled)
                .Select(entry => new { OrderId = entry.SalesOrderId!.Value, entry.ExternalReference, entry.SapDocNum })
                .ToListAsync(cancellationToken);

            links.AddRange(queued
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ExternalReference))
                .Select(entry => (entry.OrderId, entry.ExternalReference.Trim(), entry.SapDocNum)));

            var lines = await db.SalesOrderLines
                .AsNoTracking()
                .Where(line => chunk.Contains(line.SalesOrderId))
                .Select(line => new { line.SalesOrderId, line.ItemCode, line.ItemDescription, line.Quantity, line.UoMCode, line.LineTotal })
                .ToListAsync(cancellationToken);

            orderLines.AddRange(lines.Select(line => (line.SalesOrderId, new OrderLine(
                line.ItemCode.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(line.ItemDescription) ? null : line.ItemDescription.Trim(),
                line.Quantity,
                string.IsNullOrWhiteSpace(line.UoMCode) ? null : line.UoMCode.Trim(),
                line.LineTotal))));
        }

        var references = links.Select(link => link.Reference).Distinct(StringComparer.Ordinal).ToList();
        var sales = new List<VanSaleFact>();
        var saleLines = new List<VanSaleLineFact>();

        if (references.Count > 0)
        {
            var filter = VanSalesFactFilter.ForReferences(references);
            sales = await VanSalesFactReader.LoadSalesAsync(db, tax, filter, cancellationToken);
            saleLines = await VanSalesFactReader.LoadSaleLinesAsync(db, tax, filter, cancellationToken);
        }

        return new Evidence(
            links.GroupBy(link => link.OrderId)
                .ToDictionary(group => group.Key, group => group
                    .Select(link => new Link(link.Reference, link.DocNum))
                    .DistinctBy(link => link.Reference, StringComparer.Ordinal)
                    .ToList()),
            sales.GroupBy(sale => sale.ExternalReferenceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal),
            saleLines.GroupBy(line => line.ExternalReferenceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal),
            orderLines.GroupBy(line => line.OrderId)
                .ToDictionary(group => group.Key, group => group.Select(line => line.Line).ToList()));
    }

    // ── Measuring ───────────────────────────────────────────────────────────────

    /// <summary>One item of one order: what was ordered, what the van delivered, and its share of the money.</summary>
    private sealed record ItemOutcome(
        string ItemCode,
        string? Description,
        string? UoMCode,
        int LineCount,
        decimal OrderedQuantity,
        decimal SuppliedQuantity,
        decimal ShortQuantity,
        decimal OrderedGross,
        decimal ShortGross);

    /// <summary>One order, followed to its end.</summary>
    private sealed record Outcome(
        VanOrderFact Order,
        string Kind,
        decimal Ordered,
        decimal? Invoiced,
        decimal Short,
        decimal Repriced,
        DateTime? InvoicedAtCat,
        DateTime? InvoiceDay,
        double? DaysToInvoice,
        string? Reference,
        int? InvoiceDocNum,
        bool Unpriced,
        List<ItemOutcome> Items,
        List<VanSaleFact> Sales)
    {
        public bool IsInvoiced => Invoiced is not null;

        public bool ReachedSap => IsInvoiced || Order.InSap;
    }

    public static Analysis Build(Input input, Evidence evidence, VanSalesMargin? margin)
    {
        var outcomes = input.Orders.Select(order => Follow(order, evidence)).ToList();
        var byUser = outcomes.ToLookup(outcome => outcome.Order.UserId);
        var lead = LeadCurrency(outcomes);

        var overall = Summarise(outcomes, input, input.AdrAccounts.Keys.ToHashSet(), margin);

        var rows = input.Rows
            .Select(userId => new AdrConversionRowResult(
                userId,
                input.Routes.TryGetValue(userId, out var route) ? route.Code : null,
                route?.Name,
                Summarise(byUser[userId].ToList(), input, new HashSet<Guid> { userId }, margin)))
            .ToList();

        var detail = outcomes.Where(outcome => input.Detail.Contains(outcome.Order.UserId)).ToList();

        return new Analysis(
            overall,
            rows,
            BuildDays(detail, input),
            BuildShops(detail, input, lead),
            BuildItems(detail, lead),
            BuildLedger(detail),
            UnpricedInvoiced: outcomes.Count(outcome => outcome.Unpriced),
            StaleWithoutInvoice: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.NoLinkedInvoice
                                                           && outcome.Order.TradingDate <= input.TodayCat.AddDays(-2)),
            RecentAwaitingInvoice: outcomes.Count(outcome => outcome.Kind is AdrOutcomes.NoLinkedInvoice or AdrOutcomes.NotYetInSap
                                                             && outcome.Order.TradingDate > input.TodayCat.AddDays(-2)));
    }

    /// <summary>Where one order ended up.</summary>
    private static Outcome Follow(VanOrderFact order, Evidence evidence)
    {
        var lines = evidence.OrderLines.TryGetValue(order.Id, out var found) ? found : [];
        var links = evidence.LinksByOrder.TryGetValue(order.Id, out var linked) ? linked : [];
        var sales = links
            .SelectMany(link => evidence.SalesByReference.TryGetValue(link.Reference, out var made) ? made : [])
            .ToList();

        if (order.IsWithdrawn || sales.Count == 0)
        {
            var kind = order.IsWithdrawn ? AdrOutcomes.Cancelled
                : order.InSap || order.IsFulfilled ? AdrOutcomes.NoLinkedInvoice
                : AdrOutcomes.NotYetInSap;

            return new Outcome(order, kind, order.DocTotal, null, 0m, 0m, null, null, null, null, null, false,
                order.IsWithdrawn ? [] : CompareItems(lines, null, order.DocTotal), []);
        }

        var invoiced = sales.Sum(sale => sale.TotalAmount);
        var saleLines = links
            .SelectMany(link => evidence.LinesByReference.TryGetValue(link.Reference, out var made) ? made : [])
            .ToList();

        // Captured without prices and priced when the van invoiced it: the invoice is the only value the
        // order ever had, so there is no shortfall to put a price on.
        var unpriced = order.DocTotal == 0m && invoiced > 0m;
        var ordered = unpriced ? invoiced : order.DocTotal;
        var items = CompareItems(lines, saleLines, ordered);
        var shortGross = unpriced ? 0m : decimal.Round(items.Sum(item => item.ShortGross), 2);
        var repriced = ordered - invoiced - shortGross;

        var kindOfSale = shortGross > Tolerance ? AdrOutcomes.ShortSupplied
            : repriced > Tolerance ? AdrOutcomes.Repriced
            : AdrOutcomes.InvoicedInFull;

        var capturedAt = AuditService.ToCAT(order.OrderedAtUtc);
        var invoicedAt = sales.Min(sale => sale.SoldAtCat ?? sale.TradingDate);
        var days = Math.Max(0d, (invoicedAt - capturedAt).TotalDays);

        return new Outcome(
            order,
            kindOfSale,
            ordered,
            invoiced,
            shortGross,
            repriced,
            invoicedAt,
            sales.Min(sale => sale.TradingDate),
            Math.Round(days, 3, MidpointRounding.AwayFromZero),
            links[0].Reference,
            links.Select(link => link.InvoiceDocNum).FirstOrDefault(docNum => docNum is > 0),
            unpriced,
            items,
            sales);
    }

    /// <summary>
    /// Each item of an order beside what the van delivered of it. With no sale lines the order has not
    /// been invoiced and nothing is short yet; its items still carry their share of the order's value.
    /// </summary>
    private static List<ItemOutcome> CompareItems(List<OrderLine> lines, List<VanSaleLineFact>? saleLines, decimal orderGross)
    {
        var net = lines.Sum(line => line.LineTotal);
        var scale = net > 0m ? orderGross / net : 0m;

        var supplied = (saleLines ?? [])
            .GroupBy(line => line.ItemCode.Trim().ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        return lines
            .GroupBy(line => line.ItemCode)
            .Select(group =>
            {
                var orderedQuantity = group.Sum(line => line.Quantity);
                var orderedNet = group.Sum(line => line.LineTotal);
                var suppliedQuantity = saleLines is null ? 0m : supplied.GetValueOrDefault(group.Key);
                var shortQuantity = saleLines is null ? 0m : Math.Max(0m, orderedQuantity - suppliedQuantity);
                var shortNet = orderedQuantity > 0m ? orderedNet * shortQuantity / orderedQuantity : 0m;

                return new ItemOutcome(
                    group.Key,
                    group.Select(line => line.Description).FirstOrDefault(description => description is not null),
                    group.Select(line => line.UoMCode).FirstOrDefault(unit => unit is not null),
                    group.Count(),
                    orderedQuantity,
                    suppliedQuantity,
                    shortQuantity,
                    orderedNet * scale,
                    shortNet * scale);
            })
            .ToList();
    }

    private static AdrConversionSummaryResult Summarise(
        IReadOnlyList<Outcome> outcomes,
        Input input,
        IReadOnlySet<Guid> adrs,
        VanSalesMargin? margin)
    {
        var invoiced = outcomes.Where(outcome => outcome.IsInvoiced).ToList();

        var counts = new AdrFunnelCountsResult(
            Ordered: outcomes.Count,
            ReachedSap: outcomes.Count(outcome => outcome.ReachedSap),
            Invoiced: invoiced.Count,
            InvoicedInFull: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.InvoicedInFull),
            ShortSupplied: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.ShortSupplied),
            Repriced: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.Repriced),
            NoLinkedInvoice: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.NoLinkedInvoice),
            NotYetInSap: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.NotYetInSap),
            Cancelled: outcomes.Count(outcome => outcome.Kind == AdrOutcomes.Cancelled));

        // The vans that served these ADRs: the ones those who ordered are assigned to, and any other that
        // invoiced one of their orders. Their takings in the period are the whole a share is out of; an
        // ADR who ordered nothing brings no van into it.
        var accounts = outcomes
            .Select(outcome => outcome.Order.UserId)
            .Distinct()
            .Select(adr => input.AdrAccounts.GetValueOrDefault(adr))
            .Concat(invoiced.SelectMany(outcome => outcome.Sales.Select(sale => sale.VanAccountCode)))
            .Where(account => !string.IsNullOrWhiteSpace(account))
            .Select(account => account!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = invoiced
            .SelectMany(outcome => outcome.Sales.Select(sale => sale.ExternalReferenceId))
            .ToHashSet(StringComparer.Ordinal);

        var vanSales = input.PeriodSales
            .Where(sale => accounts.Contains(sale.VanAccountCode?.Trim() ?? string.Empty))
            .GroupBy(sale => Currency(sale.Currency), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var money = outcomes
            .GroupBy(outcome => outcome.Order.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sold = group.Where(outcome => outcome.IsInvoiced).ToList();
                var served = vanSales.GetValueOrDefault(group.Key) ?? [];
                var costed = margin?.Book.ForInvoices(sold.SelectMany(outcome => outcome.Sales).Select(sale => sale.SapDocEntry));

                return new AdrConversionMoneyResult(
                    Currency: group.Key,
                    Ordered: group.Sum(outcome => outcome.Ordered),
                    ReachedSap: group.Where(outcome => outcome.ReachedSap).Sum(outcome => outcome.Ordered),
                    Invoiced: sold.Sum(outcome => outcome.Invoiced!.Value),
                    ShortSupplied: sold.Sum(outcome => outcome.Short),
                    Repriced: sold.Sum(outcome => outcome.Repriced),
                    NoLinkedInvoice: Sum(group, AdrOutcomes.NoLinkedInvoice),
                    NotYetInSap: Sum(group, AdrOutcomes.NotYetInSap),
                    Cancelled: Sum(group, AdrOutcomes.Cancelled),
                    OrderedOnInvoiced: sold.Sum(outcome => outcome.Ordered),
                    VanSales: served.Sum(sale => sale.TotalAmount),
                    AdrLedVanSales: served.Where(sale => references.Contains(sale.ExternalReferenceId)).Sum(sale => sale.TotalAmount))
                {
                    GrossProfit = costed?.GrossProfit,
                    CostedNetAmount = costed?.Revenue
                };
            })
            .OrderByDescending(total => total.Ordered)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var days = invoiced
            .Where(outcome => outcome.DaysToInvoice is not null)
            .Select(outcome => outcome.DaysToInvoice!.Value)
            .Order()
            .ToList();

        var nextDay = invoiced.Count(outcome => outcome.InvoiceDay <= outcome.Order.TradingDate.AddDays(1));

        var visits = input.Visits.Where(day => adrs.Contains(day.Key.UserId)).ToList();
        var productive = outcomes
            .Where(outcome => !outcome.Order.IsWithdrawn && outcome.Order.Customer is not null)
            .Select(outcome => (outcome.Order.UserId, outcome.Order.TradingDate, Shop: outcome.Order.Customer!.ToUpperInvariant()))
            .Concat(input.AdrOwnSales
                .Where(sale => adrs.Contains(sale.UserId) && sale.RouteCustomerCode is not null)
                .Select(sale => (sale.UserId, sale.TradingDate, Shop: sale.RouteCustomerCode!.ToUpperInvariant())))
            .Distinct()
            .Count();

        return new AdrConversionSummaryResult(
            counts,
            money,
            days.Count == 0 ? null : Median(days),
            invoiced.Count == 0 ? null : (double)nextDay / invoiced.Count,
            visits.Count == 0 ? null : visits.Sum(day => day.Value.Count),
            productive);
    }

    private static decimal Sum(IEnumerable<Outcome> outcomes, string kind) =>
        outcomes.Where(outcome => outcome.Kind == kind).Sum(outcome => outcome.Ordered);

    private static List<AdrConversionDayResult> BuildDays(List<Outcome> outcomes, Input input)
    {
        var keys = outcomes.Select(outcome => new VanSalesDayKey(outcome.Order.UserId, outcome.Order.TradingDate))
            .Concat(input.Visits.Keys.Where(key => input.Detail.Contains(key.UserId)))
            .Distinct();
        var byDay = outcomes.ToLookup(outcome => new VanSalesDayKey(outcome.Order.UserId, outcome.Order.TradingDate));

        return keys
            .Select(key =>
            {
                var day = byDay[key].ToList();
                var shops = day
                    .Where(outcome => !outcome.Order.IsWithdrawn && outcome.Order.Customer is not null)
                    .Select(outcome => outcome.Order.Customer!.ToUpperInvariant())
                    .Concat(input.AdrOwnSales
                        .Where(sale => sale.Key == key && sale.RouteCustomerCode is not null)
                        .Select(sale => sale.RouteCustomerCode!.ToUpperInvariant()))
                    .Distinct()
                    .Count();

                return new AdrConversionDayResult(
                    key.UserId,
                    key.TradingDate,
                    input.Visits.TryGetValue(key, out var visited) ? visited.Count : null,
                    shops,
                    day.Count,
                    DayMoney(day));
            })
            .OrderBy(day => day.TradingDate)
            .ThenBy(day => day.UserId)
            .ToList();
    }

    private static List<AdrDayMoneyResult> DayMoney(IEnumerable<Outcome> outcomes) =>
        outcomes
            .GroupBy(outcome => outcome.Order.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AdrDayMoneyResult(
                group.Key,
                group.Sum(outcome => outcome.Ordered),
                group.Sum(outcome => outcome.Invoiced ?? 0m),
                group.Sum(outcome => outcome.Short)))
            .OrderByDescending(money => money.Ordered)
            .ThenBy(money => money.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<AdrConversionShopResult> BuildShops(List<Outcome> outcomes, Input input, string? lead)
    {
        var ordered = outcomes
            .Where(outcome => outcome.Order.Customer is not null)
            .GroupBy(outcome => (outcome.Order.UserId, Shop: outcome.Order.Customer!.ToUpperInvariant()))
            .ToDictionary(group => group.Key, group => group.ToList());

        var visits = input.Visits
            .Where(day => input.Detail.Contains(day.Key.UserId))
            .SelectMany(day => day.Value.Select(shop => (day.Key.UserId, Shop: shop.ToUpperInvariant())))
            .GroupBy(visit => visit)
            .ToDictionary(group => group.Key, group => group.Count());

        return ordered.Keys.Concat(visits.Keys)
            .Distinct()
            .Select(key =>
            {
                var shop = ordered.GetValueOrDefault(key) ?? [];
                var code = shop.Select(outcome => outcome.Order.Customer).FirstOrDefault() ?? key.Shop;
                var name = shop.Select(outcome => outcome.Order.CustomerName).FirstOrDefault(value => value is not null)
                           ?? input.VisitNames.GetValueOrDefault(key.Shop);

                return new AdrConversionShopResult(
                    key.UserId,
                    code,
                    name,
                    visits.GetValueOrDefault(key),
                    shop.Count,
                    shop.Count(outcome => outcome.IsInvoiced),
                    shop.Count(outcome => outcome.Kind == AdrOutcomes.ShortSupplied),
                    shop.Count(outcome => outcome.Kind is AdrOutcomes.NoLinkedInvoice or AdrOutcomes.NotYetInSap),
                    DayMoney(shop),
                    shop.Count == 0 ? null : shop.Max(outcome => outcome.Order.TradingDate));
            })
            .OrderByDescending(shop => LeadOrdered(shop.Money, lead))
            .ThenByDescending(shop => shop.Orders)
            .ThenByDescending(shop => shop.Visits)
            .ThenBy(shop => shop.CustomerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<AdrConversionItemResult> BuildItems(List<Outcome> outcomes, string? lead) =>
        outcomes
            .Where(outcome => !outcome.Order.IsWithdrawn)
            .SelectMany(outcome => outcome.Items.Select(item => (Outcome: outcome, Item: item)))
            .GroupBy(pair => (pair.Outcome.Order.UserId, pair.Item.ItemCode))
            .Select(group =>
            {
                var sold = group.Where(pair => pair.Outcome.IsInvoiced).ToList();
                return new AdrConversionItemResult(
                    group.Key.UserId,
                    group.Key.ItemCode,
                    group.Select(pair => pair.Item.Description).FirstOrDefault(description => description is not null),
                    group.Select(pair => pair.Item.UoMCode).FirstOrDefault(unit => unit is not null),
                    group.Sum(pair => pair.Item.LineCount),
                    group.Select(pair => pair.Outcome.Order.Customer?.ToUpperInvariant() ?? VanSalesMeasures.Unattributed).Distinct().Count(),
                    sold.Sum(pair => pair.Item.OrderedQuantity),
                    sold.Sum(pair => pair.Item.SuppliedQuantity),
                    sold.Sum(pair => pair.Item.ShortQuantity),
                    sold.Where(pair => pair.Item.ShortQuantity > 0m)
                        .Select(pair => pair.Outcome.Order.Customer?.ToUpperInvariant() ?? VanSalesMeasures.Unattributed)
                        .Distinct()
                        .Count(),
                    group
                        .GroupBy(pair => pair.Outcome.Order.Currency, StringComparer.OrdinalIgnoreCase)
                        .Select(currency => new AdrItemMoneyResult(
                            currency.Key,
                            decimal.Round(currency.Sum(pair => pair.Item.OrderedGross), 2),
                            decimal.Round(currency.Where(pair => pair.Outcome.IsInvoiced && !pair.Outcome.Unpriced)
                                .Sum(pair => pair.Item.ShortGross), 2)))
                        .OrderByDescending(money => money.Ordered)
                        .ToList());
            })
            .OrderByDescending(item => item.Money
                .Where(money => string.Equals(money.Currency, lead, StringComparison.OrdinalIgnoreCase))
                .Sum(money => money.ShortSupplied))
            .ThenByDescending(item => item.LineCount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<AdrLedgerOrderResult> BuildLedger(List<Outcome> outcomes) =>
        outcomes
            .OrderByDescending(outcome => outcome.Order.OrderedAtUtc)
            .ThenByDescending(outcome => outcome.Order.Id)
            .Select(outcome => new AdrLedgerOrderResult(
                Id: outcome.Order.Id,
                OrderNumber: outcome.Order.OrderNumber,
                UserId: outcome.Order.UserId,
                TradingDate: outcome.Order.TradingDate,
                CapturedAtCat: AuditService.ToCAT(outcome.Order.OrderedAtUtc),
                CustomerCode: outcome.Order.Customer,
                CustomerName: outcome.Order.CustomerName,
                Currency: outcome.Order.Currency,
                LineCount: outcome.Order.LineCount,
                Ordered: outcome.Ordered,
                SapOrderDocNum: outcome.Order.SapDocNum,
                Outcome: outcome.Kind,
                InvoiceReference: outcome.Reference,
                InvoiceDocNum: outcome.InvoiceDocNum,
                InvoicedAtCat: outcome.InvoicedAtCat,
                Invoiced: outcome.Invoiced,
                DaysToInvoice: outcome.DaysToInvoice,
                ShortSupplied: outcome.Short,
                Repriced: outcome.Repriced))
            .ToList();

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The currency most was ordered in, which every list ranks on so ZiG is never added to USD.</summary>
    private static string? LeadCurrency(IEnumerable<Outcome> outcomes) =>
        outcomes
            .GroupBy(outcome => outcome.Order.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(outcome => outcome.Ordered))
            .Select(group => group.Key)
            .FirstOrDefault();

    private static decimal LeadOrdered(List<AdrDayMoneyResult> money, string? lead) =>
        money.FirstOrDefault(part => string.Equals(part.Currency, lead, StringComparison.OrdinalIgnoreCase))?.Ordered ?? 0m;

    private static string Currency(string? currency) => RouteCustomerSalesReporting.NormalizeCurrency(currency);

    private static double Median(List<double> ascending) =>
        ascending.Count % 2 == 1
            ? ascending[ascending.Count / 2]
            : (ascending[ascending.Count / 2 - 1] + ascending[ascending.Count / 2]) / 2;

    /// <summary>
    /// The period a report is compared with. A run of days inside one month that starts on the 1st is
    /// set against the same days of the month before — 1–28 Oct against 1–28 Sep — because that is the
    /// comparison a manager makes; anything else against the same number of days just before it.
    /// </summary>
    public static (DateTime From, DateTime To) PreviousPeriod(DateTime from, DateTime to)
    {
        if (from.Day == 1 && to.Year == from.Year && to.Month == from.Month)
        {
            var start = from.AddMonths(-1);
            var end = new DateTime(start.Year, start.Month, Math.Min(to.Day, DateTime.DaysInMonth(start.Year, start.Month)));
            return (start, end);
        }

        var length = (to - from).Days + 1;
        var previousTo = from.AddDays(-1);
        return (previousTo.AddDays(1 - length), previousTo);
    }
}
