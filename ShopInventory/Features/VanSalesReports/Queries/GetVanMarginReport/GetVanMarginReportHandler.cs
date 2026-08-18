using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Features.Reports;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;

/// <summary>
/// Builds the margin report: what sold off the vans, what it cost, and how much of the period either
/// figure actually covers.
/// </summary>
/// <remarks>
/// Revenue is local and covers every van sale. Cost comes from the invoice lines SAP posted, so it
/// covers only the sales that reached SAP. The two are deliberately not reconciled into one
/// population — the report shows both and the gap between them, because that gap is the finding.
///
/// <b>Where the arithmetic is refused.</b> Margin is only ever stated for a currency that matches
/// the currency SAP denominates cost in. Everything else gets revenue and no margin. That rule lives
/// in one place, <see cref="BuildMargin"/>, and every margin figure on the report comes through it.
///
/// <b>Two reads go outside <see cref="VanSalesFactReader"/>.</b> Which sales reached SAP — a set of
/// keys, not a population, so it cannot become a second definition of a van sale — and the costs
/// themselves, which are not van sales at all.
/// </remarks>
public sealed class GetVanMarginReportHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IConfiguration configuration,
    ILogger<GetVanMarginReportHandler> logger
) : IRequestHandler<GetVanMarginReportQuery, ErrorOr<VanMarginReportResult>>
{
    public async Task<ErrorOr<VanMarginReportResult>> Handle(
        GetVanMarginReportQuery query,
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
                $"A van sales report covers at most {VanSalesFacts.MaximumDays} days.");
        }

        var filter = new VanSalesFactFilter(from, to, query.UserId);

        var lines = await VanSalesFactReader.LoadSaleLinesAsync(db, filter, cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.WarehouseCode))
        {
            var warehouse = query.WarehouseCode.Trim();

            lines = lines
                .Where(line => string.Equals(line.WarehouseCode, warehouse, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var posted = await LoadPostedReferencesAsync(filter, cancellationToken);
        var names = await LoadUserNamesAsync(lines.Select(line => line.UserId).Distinct(), cancellationToken);

        var costs = VanItemCostReader.CostSet.Unavailable;

        if (query.IncludeCost && lines.Count > 0)
        {
            // Linked, so a report nobody is waiting for gives its SAP slot back rather than holding
            // it for the full budget behind requests that do have someone waiting.
            using var deadline = ReportDeadline.Start(cancellationToken);

            costs = await VanItemCostReader.LoadAsync(
                sapClient,
                WarehousesOf(lines).ToList(),
                from,
                to,
                logger,
                deadline.Token);
        }

        var postingEnabled = configuration
            .GetSection(VanSalesPostingSettings.SectionName)
            .Get<VanSalesPostingSettings>()?.Enabled ?? false;

        var items = BuildItems(lines, posted, costs);

        return new VanMarginReportResult(
            FromDate: from,
            ToDate: to,
            Summary: BuildSummary(lines, posted, costs),
            Items: items,
            Vans: BuildVans(lines, posted, names),
            Quality: BuildQuality(lines, posted, items, costs, query.IncludeCost, postingEnabled));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The external references of van sales that carry a SAP document number.
    /// </summary>
    /// <remarks>
    /// A set of keys, never a population. The union of van sales is the fact reader's to define and
    /// this must not become a second answer to it — so nothing here is counted, only looked up.
    ///
    /// Both halves are read because both can post, by different routes and on differently-named
    /// columns: the offline sale carries <c>SapDocNum</c> and is drained by a background job, while
    /// a confirmed reservation carries <c>SAPDocNum</c> and was posted inline when it was made.
    /// </remarks>
    private async Task<HashSet<string>> LoadPostedReferencesAsync(
        VanSalesFactFilter filter,
        CancellationToken cancellationToken)
    {
        var offline = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.SapDocNum != null
                           && sale.DocDate >= filter.From
                           && sale.DocDate <= filter.To)
            .Select(sale => sale.ExternalReferenceId)
            .ToListAsync(cancellationToken);

        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(filter.From, filter.To);

        var online = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.SAPDocNum != null
                                  && reservation.CreatedAt >= windowStartUtc
                                  && reservation.CreatedAt < windowEndUtc)
            .Select(reservation => reservation.ExternalReferenceId)
            .ToListAsync(cancellationToken);

        return offline.Concat(online).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<Guid, VanSalesMeasures.UserName>> LoadUserNamesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Username, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            user => user.Id,
            user => new VanSalesMeasures.UserName(
                user.Username,
                string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? null
                    : $"{user.FirstName} {user.LastName}".Trim()));
    }

    // ── Margin ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Margin per currency, for the lines that can carry one.
    /// </summary>
    /// <remarks>
    /// The single place the subtraction happens, and the single place it is refused. A currency that
    /// is not the cost currency produces no row at all rather than a row with a null margin — an
    /// absent row cannot be misread as a margin of nothing, and a row of nulls beside a row of
    /// figures invites somebody to fill the gap in their head.
    ///
    /// The cost is the item's weighted unit cost times the quantity on the costable lines, rather
    /// than a cost read per line. A line's own cost is on the SAP document, but a van line and a SAP
    /// invoice line are matched only through their document, and matching them individually would
    /// need a line-number correspondence that neither ingest path preserves.
    /// </remarks>
    private static List<VanMarginMoneyResult> BuildMargin(
        IEnumerable<VanSaleLineFact> costableLines,
        VanItemCostReader.CostSet costs)
    {
        if (costs.Currency is not { } costCurrency)
        {
            return [];
        }

        return costableLines
            .Select(line => new
            {
                Line = line,
                Currency = RouteCustomerSalesReporting.NormalizeCurrency(line.Currency),
                Cost = costs.For(line.WarehouseCode, line.ItemCode)
            })
            // The refusal. Revenue in a currency SAP does not state cost in has no margin here.
            .Where(row => row.Cost is not null
                          && string.Equals(row.Currency, costCurrency, StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanMarginMoneyResult(
                Currency: group.Key,
                LineCount: group.Count(),
                Revenue: group.Sum(row => row.Line.LineTotal),
                Cost: decimal.Round(group.Sum(row => row.Line.Quantity * row.Cost!.UnitCost), 2)))
            .OrderByDescending(row => row.Revenue)
            .ThenBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Shaping ─────────────────────────────────────────────────────────────────

    private static VanMarginSummaryResult BuildSummary(
        List<VanSaleLineFact> lines,
        HashSet<string> posted,
        VanItemCostReader.CostSet costs)
    {
        var costable = Costable(lines, posted);

        return new VanMarginSummaryResult(
            ItemCount: lines.Select(line => line.ItemCode)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            VanCount: WarehousesOf(lines).Count,
            LineCount: lines.Count,
            PostedLineCount: costable.Count,
            RevenueByCurrency: VanSalesMeasures.LineMoneyByCurrency(lines),
            CostableRevenueByCurrency: VanSalesMeasures.LineMoneyByCurrency(costable),
            QuantitiesByUoM: VanSalesMeasures.QuantitiesByUoM(lines),
            CostCurrency: costs.Currency,
            MarginByCurrency: BuildMargin(costable, costs));
    }

    private static List<VanMarginItemResult> BuildItems(
        List<VanSaleLineFact> lines,
        HashSet<string> posted,
        VanItemCostReader.CostSet costs) =>
        lines
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var costable = Costable(group, posted);

                return new VanMarginItemResult(
                    ItemCode: group.Key,
                    ItemDescription: VanSalesMeasures.FirstDescription(group),
                    LineCount: group.Count(),
                    PostedLineCount: costable.Count,
                    VanCount: WarehousesOf(group).Count,
                    RevenueByCurrency: VanSalesMeasures.LineMoneyByCurrency(group),
                    CostableRevenueByCurrency: VanSalesMeasures.LineMoneyByCurrency(costable),
                    QuantitiesByUoM: VanSalesMeasures.QuantitiesByUoM(group),
                    UnitCost: UnitCostOf(costable, costs),
                    CostCurrency: costs.Currency,
                    MarginByCurrency: BuildMargin(costable, costs));
            })
            .OrderByDescending(item => item.RevenueByCurrency.Sum(money => money.Gross))
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// One unit cost for an item that may have sold off several vans at different costs, weighted by
    /// how much sold from each. Null when no costable line found a cost.
    /// </summary>
    private static decimal? UnitCostOf(
        List<VanSaleLineFact> costableLines,
        VanItemCostReader.CostSet costs)
    {
        var priced = costableLines
            .Select(line => new { line.Quantity, Cost = costs.For(line.WarehouseCode, line.ItemCode) })
            .Where(row => row.Cost is not null && row.Quantity > 0)
            .ToList();

        if (priced.Count == 0)
        {
            return null;
        }

        var quantity = priced.Sum(row => row.Quantity);

        return quantity > 0
            ? decimal.Round(priced.Sum(row => row.Quantity * row.Cost!.UnitCost) / quantity, 6)
            : null;
    }

    /// <summary>
    /// One row per van warehouse.
    /// </summary>
    /// <remarks>
    /// Grouped straight on the warehouse, with no bucket for lines that lack one: the schema and the
    /// ingest between them guarantee every van line has a warehouse, so the van rows always account
    /// for every penny in the summary.
    /// </remarks>
    private static List<VanMarginVanResult> BuildVans(
        List<VanSaleLineFact> lines,
        HashSet<string> posted,
        Dictionary<Guid, VanSalesMeasures.UserName> names) =>
        lines
            .GroupBy(line => line.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var costable = Costable(group, posted);

                // The rep who worked this warehouse most in the period. A van can change hands
                // mid-period, so this names the row rather than owning it.
                var owner = group
                    .GroupBy(line => line.UserId)
                    .OrderByDescending(byRep => byRep.Count())
                    .Select(byRep => names.GetValueOrDefault(byRep.Key))
                    .FirstOrDefault();

                return new VanMarginVanResult(
                    WarehouseCode: group.Key,
                    Username: owner?.Username,
                    FullName: owner?.FullName,
                    ItemCount: group.Select(line => line.ItemCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    LineCount: group.Count(),
                    PostedLineCount: costable.Count,
                    RevenueByCurrency: VanSalesMeasures.LineMoneyByCurrency(group),
                    CostableRevenueByCurrency: VanSalesMeasures.LineMoneyByCurrency(costable));
            })
            .OrderByDescending(van => van.RevenueByCurrency.Sum(money => money.Gross))
            .ThenBy(van => van.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static VanMarginQualityResult BuildQuality(
        List<VanSaleLineFact> lines,
        HashSet<string> posted,
        List<VanMarginItemResult> items,
        VanItemCostReader.CostSet costs,
        bool costAttempted,
        bool postingEnabled)
    {
        // The currencies the vans took that no margin could be stated for. Named rather than
        // counted: a reader whose ZiG trading is missing from the margin needs to know it was ZiG.
        var currencies = lines
            .Select(line => RouteCustomerSalesReporting.NormalizeCurrency(line.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unmatched = costs.Currency is { } costCurrency
            ? currencies
                .Where(currency => !string.Equals(currency, costCurrency, StringComparison.OrdinalIgnoreCase))
                .OrderBy(currency => currency, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        return new VanMarginQualityResult(
            LineCount: lines.Count,
            PostedLineCount: Costable(lines, posted).Count,
            ItemsWithNoDescription: items.Count(item => item.ItemDescription is null),
            ItemsWithoutCost: items.Count(item => !item.HasCost),
            ItemCount: items.Count,
            CostCurrency: costs.Currency,
            CostAttempted: costAttempted,
            CurrenciesWithoutMatchingCost: unmatched,
            PostingJobEnabled: postingEnabled);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lines whose sale reached SAP, and which a cost could therefore be found for.
    /// </summary>
    /// <remarks>
    /// The warehouse guard is belt and braces rather than a real case: both line entities mark the
    /// column required and the offline ingest refuses a batch from a rep with no assigned warehouse,
    /// so a van line always carries one. It is kept because the warehouse is the key the cost joins
    /// on, and a costable line has to have one — but it is not reported, because a figure that can
    /// never be non-zero reads as a check that passed rather than one that cannot fail.
    /// </remarks>
    private static List<VanSaleLineFact> Costable(
        IEnumerable<VanSaleLineFact> lines,
        HashSet<string> posted) =>
        lines
            .Where(line => !string.IsNullOrWhiteSpace(line.WarehouseCode)
                           && posted.Contains(line.ExternalReferenceId))
            .ToList();

    private static HashSet<string> WarehousesOf(IEnumerable<VanSaleLineFact> lines) =>
        lines
            .Select(line => line.WarehouseCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
