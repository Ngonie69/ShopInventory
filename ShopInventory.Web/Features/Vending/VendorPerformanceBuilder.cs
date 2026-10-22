using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending;

/// <summary>What the workbook is built from: the API's reads for two periods, and the page's depots.</summary>
public sealed record VendorPerformanceSources(
    VendingOverviewModel Overview,
    IReadOnlyDictionary<string, string> DepotNames,
    string? DepotFilter,
    DateTime From,
    DateTime To,
    DateTime PreviousFrom,
    DateTime PreviousTo,
    IReadOnlyCollection<RouteCustomerSalesRowModel> Sales,
    IReadOnlyCollection<RouteCustomerSalesRowModel> PreviousSales,
    IReadOnlyCollection<RouteCustomerProductSalesRowModel> Items,
    IReadOnlyCollection<RouteCustomerProductSalesRowModel> PreviousItems);

/// <summary>
/// Turns the reads into the workbook's figures. Pure: no calls, no clock, so every ranking and share can
/// be checked against numbers worked out by hand.
/// </summary>
/// <remarks>
/// Two sources meet here and are kept to their own jobs. A vendor's money is the sales summary's — the
/// same figures the page shows — and is tax-inclusive. An item's money is its lines', which a vending till
/// prices before VAT. So a vendor's items add up to their net sales (gross less VAT), and that is the
/// figure the vendor sheets print beside the gross for them to be compared on.
/// </remarks>
public static class VendorPerformanceBuilder
{
    private const string DefaultCurrency = "USD";

    /// <summary>Vendors are matched on id, or on depot and code when the sale's vendor no longer exists.</summary>
    private readonly record struct VendorKey(int? Id, string Depot, string Code)
    {
        public static VendorKey Of(int? id, string depot, string code) =>
            id is { } value ? new(value, string.Empty, string.Empty) : new(null, Normalize(depot), Normalize(code));
    }

    /// <summary>
    /// The period the report is set against. A month to date is compared with the same days of the month
    /// before — 1–21 October with 1–21 September — because that is the comparison a month is read by.
    /// Any other window is compared with the same number of days immediately before it.
    /// </summary>
    public static (DateTime From, DateTime To) PreviousWindow(DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date < from ? from : to.Date;

        if (from.Day == 1 && to.Year == from.Year && to.Month == from.Month)
        {
            var start = from.AddMonths(-1);
            var days = Math.Min(to.Day, DateTime.DaysInMonth(start.Year, start.Month));
            return (start, start.AddDays(days - 1));
        }

        var length = (to - from).Days + 1;
        return (from.AddDays(-length), from.AddDays(-1));
    }

    public static VendorPerformanceReport Build(VendorPerformanceSources sources)
    {
        var depots = sources.Overview.Depots
            .Where(depot => string.IsNullOrEmpty(sources.DepotFilter)
                || string.Equals(depot.BusinessPartnerCode, sources.DepotFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var depotCodes = depots.Select(depot => depot.BusinessPartnerCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string DepotName(string code) => sources.DepotNames.TryGetValue(code, out var name) ? name : code;

        var sales = sources.Sales.Where(row => depotCodes.Contains(row.AssignedBusinessPartnerCode)).ToList();
        var items = sources.Items.Where(row => depotCodes.Contains(row.AssignedBusinessPartnerCode)).ToList();
        var previousItems = sources.PreviousItems.Where(row => depotCodes.Contains(row.AssignedBusinessPartnerCode)).ToList();
        var previousSales = sources.PreviousSales
            .Where(row => depotCodes.Contains(row.AssignedBusinessPartnerCode))
            .GroupBy(row => KeyOf(row))
            .ToDictionary(group => group.Key, group => group.ToList());

        var currencies = OrderCurrencies(sales, sources.PreviousSales, items, previousItems);
        var currency = currencies[0];

        var vendorsById = sources.Overview.Vendors.ToDictionary(vendor => vendor.Id);
        var itemsByVendor = items.GroupBy(KeyOf).ToDictionary(group => group.Key, group => group.ToList());

        // ── Vendors ──
        var vendors = sales
            // A removed vendor is listed only when there is money of theirs in the period to account for.
            .Where(row => (row.IsActive && !row.IsDeleted) || row.SaleCount > 0)
            .Select(row => BuildVendor(row, previousSales.GetValueOrDefault(KeyOf(row)) ?? [],
                itemsByVendor.GetValueOrDefault(KeyOf(row)) ?? [], vendorsById, DepotName(row.AssignedBusinessPartnerCode), currency))
            .ToList();

        var grandGross = vendors.Sum(vendor => vendor.Gross[currency]);
        var ranked = vendors
            .OrderByDescending(vendor => vendor.Gross[currency])
            .ThenByDescending(vendor => vendor.Invoices)
            // Among those who sold nothing, the most recently active first and the never-sold last.
            .ThenByDescending(vendor => vendor.LastSaleAt ?? DateTime.MinValue)
            .ThenBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < ranked.Count; index++)
        {
            ranked[index].Rank = index + 1;
            ranked[index].ShareOfAll = Ratio(ranked[index].Gross[currency], grandGross);
        }

        // ── Depots ──
        var depotRows = depots.Select(depot =>
        {
            var depotVendors = ranked
                .Where(vendor => string.Equals(vendor.DepotCode, depot.BusinessPartnerCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var depotGross = depotVendors.Sum(vendor => vendor.Gross[currency]);
            for (var index = 0; index < depotVendors.Count; index++)
            {
                depotVendors[index].RankInDepot = index + 1;
                depotVendors[index].ShareOfDepot = Ratio(depotVendors[index].Gross[currency], depotGross);
            }

            var selling = depotVendors.Count(vendor => vendor.Invoices > 0);
            var invoices = depotVendors.Sum(vendor => vendor.Invoices);

            // Every vendor's previous takings, including vendors not listed now — removed since and silent
            // in this period. They still sold then, and leaving them out would overstate the growth.
            var previousRows = previousSales.Values
                .SelectMany(rows => rows)
                .Where(sale => SameDepot(sale, depot.BusinessPartnerCode))
                .ToList();
            var previousGross = new CurrencyAmounts();
            foreach (var total in previousRows.SelectMany(sale => sale.TotalsByCurrency))
            {
                previousGross.Add(Currency(total.Currency), total.Gross);
            }

            var row = new DepotPerformance
            {
                Code = depot.BusinessPartnerCode,
                Name = DepotName(depot.BusinessPartnerCode),
                WarehouseCodes = depot.WarehouseCodes,
                Cashiers = depot.ActiveCashierCount,
                ActiveVendors = depot.ActiveVendorCount,
                SellingVendors = selling,
                Invoices = invoices,
                PreviousInvoices = previousRows.Sum(sale => sale.SaleCount),
                Lines = depotVendors.Sum(vendor => vendor.Lines),
                Share = Ratio(depotGross, grandGross),
                Change = Change(depotGross, previousGross[currency]),
                AveragePerSellingVendor = selling > 0 ? depotGross / selling : null,
                AverageInvoice = invoices > 0 ? depotGross / invoices : null,
                Vendors = depotVendors,
                Products = RankProducts(
                    items.Where(item => SameDepot(item, depot.BusinessPartnerCode)),
                    previousItems.Where(item => SameDepot(item, depot.BusinessPartnerCode)),
                    currency, selling, []),
            };

            foreach (var vendor in depotVendors)
            {
                row.Gross.Add(vendor.Gross);
                row.Net.Add(vendor.Net);
            }

            row.PreviousGross.Add(previousGross);
            return row;
        }).ToList();

        // ── Items ──
        var sellingVendors = ranked.Count(vendor => vendor.Invoices > 0);
        var products = RankProducts(items, previousItems, currency, sellingVendors,
            depotRows.Select(depot => depot.Code).ToList());

        // Laid out the way the depot sheets are: depot by depot, best vendor first, best item first.
        var vendorByKey = ranked.ToDictionary(vendor => VendorKey.Of(vendor.Id, vendor.DepotCode, vendor.Code));
        var depotOrder = depotRows
            .Select((depot, index) => (depot.Code, index))
            .ToDictionary(pair => pair.Code, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        var vendorProducts = items
            .GroupBy(KeyOf)
            .SelectMany(group =>
            {
                var vendor = vendorByKey.GetValueOrDefault(group.Key);
                var totals = group
                    .GroupBy(item => Currency(item.Currency), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(byCurrency => byCurrency.Key, byCurrency => byCurrency.Sum(item => item.LineTotal), StringComparer.OrdinalIgnoreCase);

                return group.Select(item => (
                    Depot: depotOrder.GetValueOrDefault(item.AssignedBusinessPartnerCode, int.MaxValue),
                    Rank: vendor?.RankInDepot ?? int.MaxValue,
                    Row: new VendorProductPerformance
                    {
                        DepotName = vendor?.DepotName ?? DepotName(item.AssignedBusinessPartnerCode),
                        VendorCode = vendor?.Code ?? item.RouteCustomerCode,
                        VendorName = vendor?.Name ?? item.RouteCustomerCode,
                        ItemCode = item.ItemCode,
                        Description = item.ItemDescription,
                        UoMCode = item.UoMCode,
                        Currency = Currency(item.Currency),
                        Quantity = item.Quantity,
                        Net = item.LineTotal,
                        ShareOfVendor = Ratio(item.LineTotal, totals.GetValueOrDefault(Currency(item.Currency))),
                        Lines = item.LineCount,
                        LastSoldAt = item.LastSoldAt,
                    }));
            })
            .OrderBy(entry => entry.Depot)
            .ThenBy(entry => entry.Rank)
            .ThenBy(entry => entry.Row.VendorCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => string.Equals(entry.Row.Currency, currency, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(entry => entry.Row.Net)
            .Select(entry => entry.Row)
            .ToList();

        // ── Totals ──
        var totalInvoices = depotRows.Sum(depot => depot.Invoices);
        var previousTotal = depotRows.Sum(depot => depot.PreviousGross[currency]);
        var totals = new VendorPerformanceTotals
        {
            Invoices = totalInvoices,
            PreviousInvoices = depotRows.Sum(depot => depot.PreviousInvoices),
            ActiveVendors = depotRows.Sum(depot => depot.ActiveVendors),
            SellingVendors = sellingVendors,
            ItemsSold = products.Count,
            Change = Change(grandGross, previousTotal),
            AverageInvoice = totalInvoices > 0 ? grandGross / totalInvoices : null,
        };
        foreach (var depot in depotRows)
        {
            totals.Gross.Add(depot.Gross);
            totals.Net.Add(depot.Net);
            totals.PreviousGross.Add(depot.PreviousGross);
        }

        return new VendorPerformanceReport
        {
            From = sources.From,
            To = sources.To,
            PreviousFrom = sources.PreviousFrom,
            PreviousTo = sources.PreviousTo,
            Currency = currency,
            Currencies = currencies,
            ScopeLabel = depots.Count == 1 && !string.IsNullOrEmpty(sources.DepotFilter)
                ? DepotName(depots[0].BusinessPartnerCode)
                : "All depots",
            Totals = totals,
            Depots = depotRows,
            Vendors = ranked,
            Products = products,
            VendorProducts = vendorProducts,
        };
    }

    private static VendorPerformance BuildVendor(
        RouteCustomerSalesRowModel row,
        List<RouteCustomerSalesRowModel> previous,
        List<RouteCustomerProductSalesRowModel> items,
        IReadOnlyDictionary<int, RouteCustomerModel> vendorsById,
        string depotName,
        string currency)
    {
        var record = row.RouteCustomerId is { } id ? vendorsById.GetValueOrDefault(id) : null;

        var gross = new CurrencyAmounts();
        var net = new CurrencyAmounts();
        foreach (var total in row.TotalsByCurrency)
        {
            gross.Add(Currency(total.Currency), total.Gross);
            // A vending sale carries its own VAT split, so gross less VAT is its net — what its lines add to.
            net.Add(Currency(total.Currency), total.Gross - total.Vat);
        }

        var previousGross = new CurrencyAmounts();
        foreach (var total in previous.SelectMany(sale => sale.TotalsByCurrency))
        {
            previousGross.Add(Currency(total.Currency), total.Gross);
        }

        var topItem = items
            .Where(item => string.Equals(Currency(item.Currency), currency, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.LineTotal)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? items.OrderByDescending(item => item.LineTotal).FirstOrDefault();

        var vendor = new VendorPerformance
        {
            Id = row.RouteCustomerId,
            Code = record?.Code ?? row.Code,
            Name = record is null ? (string.IsNullOrWhiteSpace(row.Name) ? row.Code : row.Name) : FullName(record),
            Phone = string.IsNullOrWhiteSpace(record?.Phone ?? row.Phone) ? null : (record?.Phone ?? row.Phone)!.Trim(),
            DepotCode = row.AssignedBusinessPartnerCode,
            DepotName = depotName,
            IsActive = row.IsActive && !row.IsDeleted,
            Standing = row.SaleCount > 0 ? VendorStanding.Selling
                : row.LastSaleAt is null ? VendorStanding.NeverSold
                : VendorStanding.Quiet,
            Invoices = row.SaleCount,
            PreviousInvoices = previous.Sum(sale => sale.SaleCount),
            Lines = row.LineCount,
            AverageInvoice = row.SaleCount > 0 ? gross[currency] / row.SaleCount : null,
            Change = Change(gross[currency], previousGross[currency]),
            ItemCount = items.Select(item => Normalize(item.ItemCode)).Distinct().Count(),
            TopItemCode = topItem?.ItemCode,
            TopItemDescription = topItem?.ItemDescription,
            LastSaleAt = row.LastSaleAt,
            DaysSinceLastSale = row.DaysSinceLastSale,
        };

        vendor.Gross.Add(gross);
        vendor.Net.Add(net);
        vendor.PreviousGross.Add(previousGross);
        return vendor;
    }

    private static List<ProductPerformance> RankProducts(
        IEnumerable<RouteCustomerProductSalesRowModel> items,
        IEnumerable<RouteCustomerProductSalesRowModel> previousItems,
        string currency,
        int sellingVendors,
        IReadOnlyList<string> depotCodes)
    {
        var previousByItem = previousItems
            .GroupBy(item => Normalize(item.ItemCode))
            .ToDictionary(group => group.Key, group => group.ToList());

        var groups = items
            .GroupBy(item => Normalize(item.ItemCode))
            .Select(group =>
            {
                var inCurrency = group.Where(item => string.Equals(Currency(item.Currency), currency, StringComparison.OrdinalIgnoreCase)).ToList();
                return new
                {
                    Rows = group.ToList(),
                    Value = inCurrency.Sum(item => item.LineTotal),
                    ValueQuantity = inCurrency.Sum(item => item.Quantity),
                    Quantity = group.Sum(item => item.Quantity),
                    Lines = group.Sum(item => item.LineCount),
                };
            })
            .OrderByDescending(group => group.Value)
            .ThenByDescending(group => group.Lines)
            .ThenBy(group => group.Rows[0].ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = groups.Sum(group => group.Value);
        var products = new List<ProductPerformance>(groups.Count);
        var before = 0m;

        foreach (var group in groups)
        {
            var share = Ratio(group.Value, total);
            var shareBefore = total > 0 ? before / total : 1m;
            before += group.Value;

            var previous = previousByItem.GetValueOrDefault(Normalize(group.Rows[0].ItemCode)) ?? [];
            var product = new ProductPerformance
            {
                Rank = products.Count + 1,
                ItemCode = group.Rows[0].ItemCode,
                Description = group.Rows.Select(item => item.ItemDescription).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                UoMCode = group.Rows.Select(item => item.UoMCode).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                Quantity = group.Quantity,
                Share = share,
                CumulativeShare = total > 0 ? before / total : null,
                // Cut on what came before the item, so the best seller is always an A however big it is.
                Class = group.Value <= 0 ? 'C' : shareBefore < 0.80m ? 'A' : shareBefore < 0.95m ? 'B' : 'C',
                Lines = group.Lines,
                Vendors = group.Rows.Select(KeyOf).Distinct().Count(),
                Reach = sellingVendors > 0 ? (decimal)group.Rows.Select(KeyOf).Distinct().Count() / sellingVendors : null,
                AveragePrice = group.ValueQuantity > 0 ? group.Value / group.ValueQuantity : null,
                Change = Change(group.Value, previous
                    .Where(item => string.Equals(Currency(item.Currency), currency, StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.LineTotal)),
                NetByDepot = depotCodes.ToDictionary(
                    code => code,
                    code => group.Rows
                        .Where(item => SameDepot(item, code) && string.Equals(Currency(item.Currency), currency, StringComparison.OrdinalIgnoreCase))
                        .Sum(item => item.LineTotal),
                    StringComparer.OrdinalIgnoreCase),
                LastSoldAt = group.Rows.Max(item => item.LastSoldAt),
            };

            foreach (var item in group.Rows)
            {
                product.Net.Add(Currency(item.Currency), item.LineTotal);
            }

            foreach (var item in previous)
            {
                product.PreviousNet.Add(Currency(item.Currency), item.LineTotal);
            }

            products.Add(product);
        }

        return products;
    }

    /// <summary>
    /// The currency most invoices were written in first; USD when nothing was sold at all.
    /// </summary>
    /// <remarks>
    /// Counted in invoices, not money: ZWG 2,000 is not more trade than USD 400, and comparing the two
    /// amounts to pick one would be adding currencies by another name.
    /// </remarks>
    private static List<string> OrderCurrencies(
        IEnumerable<RouteCustomerSalesRowModel> sales,
        IEnumerable<RouteCustomerSalesRowModel> previousSales,
        IEnumerable<RouteCustomerProductSalesRowModel> items,
        IEnumerable<RouteCustomerProductSalesRowModel> previousItems)
    {
        var invoices = sales.SelectMany(row => row.TotalsByCurrency)
            .GroupBy(total => Currency(total.Currency), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(total => total.SaleCount), StringComparer.OrdinalIgnoreCase);

        var all = invoices.Keys
            .Concat(previousSales.SelectMany(row => row.TotalsByCurrency).Select(total => Currency(total.Currency)))
            .Concat(items.Concat(previousItems).Select(item => Currency(item.Currency)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(code => invoices.GetValueOrDefault(code))
            .ThenBy(code => string.Equals(code, DefaultCurrency, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return all.Count == 0 ? [DefaultCurrency] : all;
    }

    private static VendorKey KeyOf(RouteCustomerSalesRowModel row) =>
        VendorKey.Of(row.RouteCustomerId, row.AssignedBusinessPartnerCode, row.Code);

    private static VendorKey KeyOf(RouteCustomerProductSalesRowModel row) =>
        VendorKey.Of(row.RouteCustomerId, row.AssignedBusinessPartnerCode, row.RouteCustomerCode);

    private static bool SameDepot(RouteCustomerSalesRowModel row, string depotCode) =>
        string.Equals(row.AssignedBusinessPartnerCode, depotCode, StringComparison.OrdinalIgnoreCase);

    private static bool SameDepot(RouteCustomerProductSalesRowModel row, string depotCode) =>
        string.Equals(row.AssignedBusinessPartnerCode, depotCode, StringComparison.OrdinalIgnoreCase);

    private static string FullName(RouteCustomerModel vendor) =>
        string.IsNullOrWhiteSpace(vendor.Surname) ? vendor.Name : $"{vendor.Name} {vendor.Surname}";

    private static decimal? Ratio(decimal part, decimal whole) => whole > 0 ? part / whole : null;

    /// <summary>Null when there was nothing to grow from: a vendor new to selling has no percentage change.</summary>
    private static decimal? Change(decimal current, decimal previous) =>
        previous > 0 ? (current - previous) / previous : null;

    private static string Currency(string? code) =>
        string.IsNullOrWhiteSpace(code) ? DefaultCurrency : code.Trim().ToUpperInvariant();

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
