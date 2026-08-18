using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;

/// <summary>
/// Builds the local half of the margin report: what sold, and what share of it SAP could cost.
/// </summary>
/// <remarks>
/// Reads nothing from SAP. That is the point of the split — the cost join is the part that cannot be
/// written until the invoice-line cost column is established, and everything else is local, correct
/// now, and the denominator that half will need.
///
/// One read here goes outside <see cref="VanSalesFactReader"/>: which sales reached SAP. The reader
/// answers "what sold" and deliberately says nothing about a sale's posting state, so the posted set
/// is loaded separately and joined on the external reference. It is a set of keys rather than a
/// second population, so it cannot become a second definition of a van sale.
/// </remarks>
public sealed class GetVanMarginReportHandler(
    ApplicationDbContext db,
    IConfiguration configuration
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

        var postingEnabled = configuration
            .GetSection(VanSalesPostingSettings.SectionName)
            .Get<VanSalesPostingSettings>()?.Enabled ?? false;

        return new VanMarginReportResult(
            FromDate: from,
            ToDate: to,
            Summary: BuildSummary(lines, posted),
            Items: BuildItems(lines, posted),
            Vans: BuildVans(lines, posted, names),
            Quality: BuildQuality(lines, posted, postingEnabled));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The external references of van sales that carry a SAP document number.
    /// </summary>
    /// <remarks>
    /// A set of keys, never a population. The union of van sales is the fact reader's to define and
    /// this must not become a second answer to it — so nothing here is counted, only looked up.
    ///
    /// Both halves are read because both can post, by different routes and on different columns:
    /// the offline sale carries <c>SapDocNum</c> and is drained by a background job, while a
    /// confirmed reservation carries <c>SAPDocNum</c> and was posted inline when it was made.
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

    // ── Shaping ─────────────────────────────────────────────────────────────────

    private static VanMarginSummaryResult BuildSummary(
        List<VanSaleLineFact> lines,
        HashSet<string> posted)
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
            QuantitiesByUoM: VanSalesMeasures.QuantitiesByUoM(lines));
    }

    private static List<VanMarginItemResult> BuildItems(
        List<VanSaleLineFact> lines,
        HashSet<string> posted) =>
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
                    // The seam. Both stay null until the invoice-line cost column is settled; a zero
                    // in either would report an item that costs nothing to sell.
                    UnitCost: null,
                    CostByCurrency: null);
            })
            .OrderByDescending(item => item.RevenueByCurrency.Sum(money => money.Gross))
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        bool postingEnabled) =>
        new(
            LineCount: lines.Count,
            PostedLineCount: Costable(lines, posted).Count,
            ItemsWithNoDescription: lines
                .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
                .Count(group => VanSalesMeasures.FirstDescription(group) is null),
            PostingJobEnabled: postingEnabled);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lines whose sale reached SAP, and which a cost could therefore be found for.
    /// </summary>
    /// <remarks>
    /// The warehouse guard is belt and braces rather than a real case: both line entities mark the
    /// column required and the offline ingest refuses a batch from a rep with no assigned warehouse,
    /// so a van line always carries one. It is kept because the warehouse is the key the cost will
    /// join on, and a costable line has to have one — but it is not reported, because a figure that
    /// can never be non-zero reads as a check that passed rather than one that cannot fail.
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
