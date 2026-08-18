using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesExceptionsReport;

/// <summary>
/// Builds the settlement split and the register of van documents the reporting union cannot see.
/// </summary>
/// <remarks>
/// Three of the reads here deliberately go around <see cref="VanSalesFactReader"/>.
///
/// The reader answers "what sold", and it answers it by taking confirmed reservations and ingested
/// offline sales. Everything in the unseen and held sections is, by definition, a document the reader
/// excludes — so asking the reader for them would return nothing and the page would read clean. The
/// direct reads are the report, not a shortcut around it.
///
/// The cost of that is a standing hazard: this file now holds a second query over the same two
/// tables, and if it drifted into being used as "the van sales" it would be a second definition of a
/// sale. It is scoped so it cannot be. The unseen read takes only <em>non</em>-confirmed reservations
/// and the held read only <em>non</em>-consolidated sales, so the two populations are disjoint from
/// the reader's by construction rather than by convention.
/// </remarks>
public sealed class GetVanSalesExceptionsReportHandler(
    ApplicationDbContext db,
    IConfiguration configuration
) : IRequestHandler<GetVanSalesExceptionsReportQuery, ErrorOr<VanSalesExceptionsReportResult>>
{
    /// <summary>How many held sales a rep's row names an error from before it stops.</summary>
    private const int LastErrorLength = 300;

    public async Task<ErrorOr<VanSalesExceptionsReportResult>> Handle(
        GetVanSalesExceptionsReportQuery query,
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

        var sales = await VanSalesFactReader.LoadSalesAsync(db, filter, cancellationToken);
        var lines = await VanSalesFactReader.LoadSaleLinesAsync(db, filter, cancellationToken);

        var unseenRows = await LoadUnseenAsync(filter, cancellationToken);
        var heldRows = await LoadHeldAsync(filter, cancellationToken);
        var handover = await LoadReceiptHandoverAsync(filter, cancellationToken);

        var repIds = sales.Select(sale => sale.UserId)
            .Concat(unseenRows.Select(row => row.UserId).Where(id => id.HasValue).Select(id => id!.Value))
            .Concat(heldRows.Select(row => row.UserId).Where(id => id.HasValue).Select(id => id!.Value))
            .Distinct()
            .ToList();

        var reps = await LoadRepsAsync(repIds, cancellationToken);

        var unseen = BuildUnseen(unseenRows, reps);
        var held = BuildHeld(heldRows, reps);
        var hygiene = BuildHygiene(sales, lines, reps);

        var postingEnabled = configuration
            .GetSection(VanSalesPostingSettings.SectionName)
            .Get<VanSalesPostingSettings>()?.Enabled ?? false;

        var result = new VanSalesExceptionsReportResult(
            FromDate: from,
            ToDate: to,
            Summary: BuildSummary(sales, lines, unseen, held),
            Tender: BuildTender(sales),
            TenderByRep: BuildTenderByRep(sales, reps),
            Unseen: unseen,
            Held: held,
            ReceiptHandover: handover,
            Hygiene: hygiene,
            Quality: BuildQuality(sales, lines, unseen, held, handover, postingEnabled));

        return result;
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Van reservations in the window that never reached <c>Confirmed</c>.
    /// </summary>
    /// <remarks>
    /// Keyed on <c>CreatedAt</c>, a UTC instant, converted through the same window the fact reader
    /// uses — so a document and the sale it failed to become are filed on the same trading day.
    ///
    /// No <c>CreatedBy is not null</c> filter, unlike the reader. The reader can afford to drop an
    /// unattributable sale because it has thousands of others; here an unattributed document is
    /// itself the finding, so it is kept and reported against a null rep.
    /// </remarks>
    private async Task<List<UnseenRow>> LoadUnseenAsync(
        VanSalesFactFilter filter,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(filter.From, filter.To);

        var rows = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status != ReservationStatus.Confirmed
                                  && reservation.CreatedAt >= windowStartUtc
                                  && reservation.CreatedAt < windowEndUtc)
            .Select(reservation => new
            {
                reservation.Status,
                reservation.CreatedBy,
                reservation.CreatedAt,
                reservation.TotalValue,
                reservation.Currency
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new UnseenRow(
                Status: string.IsNullOrWhiteSpace(row.Status) ? "Unknown" : row.Status.Trim(),
                UserId: VanSalesFacts.TryResolveRep(row.CreatedBy, out var userId) ? userId : null,
                CapturedAt: row.CreatedAt,
                Gross: row.TotalValue,
                Currency: RouteCustomerSalesReporting.NormalizeCurrency(row.Currency)))
            .Where(row => filter.UserId is null || row.UserId == filter.UserId)
            .ToList();
    }

    /// <summary>
    /// Offline van sales in the window that have not been consolidated into SAP.
    /// </summary>
    /// <remarks>
    /// <c>DocDate</c> is a bare CAT trading day and is compared as one — no clock conversion, because
    /// the handset already decided which day the sale belongs to.
    /// </remarks>
    private async Task<List<HeldRow>> LoadHeldAsync(
        VanSalesFactFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated
                           && sale.DocDate >= filter.From
                           && sale.DocDate <= filter.To)
            .Select(sale => new
            {
                sale.CreatedBy,
                sale.DocDate,
                sale.TotalAmount,
                sale.Currency,
                sale.PostingAttempts,
                sale.LastPostingError
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new HeldRow(
                UserId: VanSalesFacts.TryResolveRep(row.CreatedBy, out var userId) ? userId : null,
                DocDate: row.DocDate.Date,
                Gross: row.TotalAmount,
                Currency: RouteCustomerSalesReporting.NormalizeCurrency(row.Currency),
                PostingAttempts: row.PostingAttempts,
                LastPostingError: row.LastPostingError))
            .Where(row => filter.UserId is null || row.UserId == filter.UserId)
            .ToList();
    }

    /// <summary>
    /// The spread of receipt-handover states across every van sale in the window.
    /// </summary>
    /// <remarks>
    /// Counts signatures alongside, because the status alone cannot be trusted: a sale whose upload
    /// carried no signature can never be handed over regardless of what its status says, and the
    /// default value of the column means "nothing to submit".
    /// </remarks>
    private async Task<List<VanSalesReceiptHandoverResult>> LoadReceiptHandoverAsync(
        VanSalesFactFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.DocDate >= filter.From
                           && sale.DocDate <= filter.To)
            .Select(sale => new
            {
                sale.CreatedBy,
                sale.ReceiptIngestStatus,
                sale.DocDate,
                HasSignature = sale.DeviceSignatureValue != null && sale.DeviceSignatureValue != ""
            })
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => filter.UserId is null
                          || (VanSalesFacts.TryResolveRep(row.CreatedBy, out var userId)
                              && userId == filter.UserId))
            .GroupBy(row => row.ReceiptIngestStatus)
            .Select(group => new VanSalesReceiptHandoverResult(
                Status: group.Key.ToString(),
                SaleCount: group.Count(),
                WithSignature: group.Count(row => row.HasSignature),
                EarliestDocDate: group.Min(row => row.DocDate).Date,
                LatestDocDate: group.Max(row => row.DocDate).Date))
            .OrderByDescending(row => row.SaleCount)
            .ThenBy(row => row.Status, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<Guid, RepRow>> LoadRepsAsync(
        List<Guid> repIds,
        CancellationToken cancellationToken)
    {
        if (repIds.Count == 0)
        {
            return [];
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(user => repIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Username, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            user => user.Id,
            user => new RepRow(
                user.Username,
                string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? null
                    : $"{user.FirstName} {user.LastName}".Trim()));
    }

    // ── D2: settlement ──────────────────────────────────────────────────────────

    /// <summary>
    /// The period's tender split, per currency.
    /// </summary>
    /// <remarks>
    /// A sale with no recorded payment method classifies as <c>Other</c>, the same bucket a swipe
    /// falls into. That is right for the classifier — it answers "which column of the cash-up does
    /// this belong in" — but wrong for a reader, because one is a banking arrangement and the other is
    /// a capture failure. They are split into two rows here and the row says which it is.
    /// </remarks>
    private static List<VanSalesTenderResult> BuildTender(List<VanSaleFact> sales) =>
        sales
            .GroupBy(sale => (
                Currency: RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency),
                Tender: sale.Tender,
                Untendered: string.IsNullOrWhiteSpace(sale.PaymentMethod)))
            .Select(group => new VanSalesTenderResult(
                Currency: group.Key.Currency,
                Tender: group.Key.Tender.ToString(),
                Untendered: group.Key.Untendered,
                DocumentCount: group.Count(),
                Gross: group.Sum(sale => sale.TotalAmount)))
            .OrderBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.Gross)
            .ToList();

    private static List<VanSalesRepTenderResult> BuildTenderByRep(
        List<VanSaleFact> sales,
        Dictionary<Guid, RepRow> reps) =>
        sales
            .GroupBy(sale => (
                sale.UserId,
                Currency: RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency)))
            .Select(group =>
            {
                var rep = reps.GetValueOrDefault(group.Key.UserId);
                var untendered = group.Where(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)).ToList();

                return new VanSalesRepTenderResult(
                    UserId: group.Key.UserId,
                    Username: rep?.Username ?? group.Key.UserId.ToString(),
                    FullName: rep?.FullName,
                    Currency: group.Key.Currency,
                    DocumentCount: group.Count(),
                    Gross: group.Sum(sale => sale.TotalAmount),
                    CashGross: GrossOf(group, VanSalesTender.Cash),
                    EcocashGross: GrossOf(group, VanSalesTender.Ecocash),
                    InnbucksGross: GrossOf(group, VanSalesTender.Innbucks),
                    OtherGross: GrossOf(group, VanSalesTender.Other),
                    UntenderedGross: untendered.Sum(sale => sale.TotalAmount),
                    UntenderedCount: untendered.Count);
            })
            .OrderByDescending(row => row.Gross)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static decimal GrossOf(IEnumerable<VanSaleFact> sales, VanSalesTender tender) =>
        sales.Where(sale => sale.Tender == tender).Sum(sale => sale.TotalAmount);

    // ── The unseen and the held ─────────────────────────────────────────────────

    private static List<VanSalesUnseenResult> BuildUnseen(
        List<UnseenRow> rows,
        Dictionary<Guid, RepRow> reps) =>
        rows
            // Case-insensitively, because IsLostSale compares that way. Only ReservationStatus's own
            // constants write this column today, so the two cannot disagree in practice — but a
            // writer with different casing would otherwise split one state into two rows, both
            // flagged, and the register would report an outage twice.
            .GroupBy(row => (Status: row.Status.ToUpperInvariant(), row.UserId))
            .Select(group =>
            {
                var rep = group.Key.UserId is { } id ? reps.GetValueOrDefault(id) : null;

                return new VanSalesUnseenResult(
                    Status: group.First().Status,
                    UserId: group.Key.UserId,
                    Username: rep?.Username,
                    FullName: rep?.FullName,
                    DocumentCount: group.Count(),
                    EarliestCapturedAt: group.Min(row => row.CapturedAt),
                    LatestCapturedAt: group.Max(row => row.CapturedAt),
                    Exposure: ExposureOf(group.Select(row => (row.Currency, row.Gross))));
            })
            // The outage signature first, then the biggest populations: a reader opens this section
            // because something is wrong, and the lost sales are the row that says so.
            .OrderByDescending(row => row.IsLostSale)
            .ThenByDescending(row => row.DocumentCount)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<VanSalesHeldResult> BuildHeld(
        List<HeldRow> rows,
        Dictionary<Guid, RepRow> reps)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        return rows
            .GroupBy(row => row.UserId)
            .Select(group =>
            {
                var rep = group.Key is { } id ? reps.GetValueOrDefault(id) : null;
                var oldest = group.Min(row => row.DocDate);
                var lastError = group
                    .Where(row => !string.IsNullOrWhiteSpace(row.LastPostingError))
                    .OrderByDescending(row => row.DocDate)
                    .Select(row => row.LastPostingError!.Trim())
                    .FirstOrDefault();

                return new VanSalesHeldResult(
                    UserId: group.Key,
                    Username: rep?.Username,
                    FullName: rep?.FullName,
                    SaleCount: group.Count(),
                    OldestDocDate: oldest,
                    OldestAgeDays: (int)(today - oldest).TotalDays,
                    AttemptedCount: group.Count(row => row.PostingAttempts > 0),
                    FailedCount: group.Count(row => !string.IsNullOrWhiteSpace(row.LastPostingError)),
                    LastError: lastError is null
                        ? null
                        : lastError.Length <= LastErrorLength
                            ? lastError
                            : lastError[..LastErrorLength],
                    Exposure: ExposureOf(group.Select(row => (row.Currency, row.Gross))));
            })
            .OrderByDescending(row => row.OldestAgeDays ?? 0)
            .ThenByDescending(row => row.SaleCount)
            .ToList();
    }

    // ── Capture hygiene ─────────────────────────────────────────────────────────

    private static List<VanSalesHygieneResult> BuildHygiene(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        Dictionary<Guid, RepRow> reps)
    {
        var linesByRep = lines
            .GroupBy(line => line.UserId)
            .ToDictionary(group => group.Key, group => group.ToList());

        return sales
            .GroupBy(sale => sale.UserId)
            .Select(group =>
            {
                var rep = reps.GetValueOrDefault(group.Key);
                var repLines = linesByRep.GetValueOrDefault(group.Key) ?? [];

                return new VanSalesHygieneResult(
                    UserId: group.Key,
                    Username: rep?.Username ?? group.Key.ToString(),
                    FullName: rep?.FullName,
                    SaleCount: group.Count(),
                    WithoutTender: group.Count(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)),
                    WithoutOutlet: group.Count(sale => sale.Outlet is null),
                    LineCount: repLines.Count,
                    LinesWithoutValue: CountLinesWithoutValue(repLines));
            })
            // Worst first: this is a worklist, and a clean rep is the row nobody needs to read.
            .OrderByDescending(row => row.WithoutTender + row.WithoutOutlet + row.LinesWithoutValue)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lines that moved stock and charged nothing.
    /// </summary>
    /// <remarks>
    /// Both capture paths permit it: the offline validator checks quantity and never price, the online
    /// van command has no validator at all, and both check constraints are non-negative rather than
    /// positive. So a zero here is a real zero — a free issue, a keying slip or a promotion nobody
    /// recorded as one — and not a rounding artefact.
    /// </remarks>
    private static int CountLinesWithoutValue(IEnumerable<VanSaleLineFact> lines) =>
        lines.Count(line => line.Quantity > 0 && line.LineTotal == 0);

    // ── Summary and quality ─────────────────────────────────────────────────────

    private static VanSalesExceptionsSummaryResult BuildSummary(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        List<VanSalesUnseenResult> unseen,
        List<VanSalesHeldResult> held)
    {
        var oldestHeld = held.Count == 0 ? null : held.Max(row => row.OldestAgeDays);

        return new VanSalesExceptionsSummaryResult(
            SaleCount: sales.Count,
            RepCount: sales.Select(sale => sale.UserId).Distinct().Count(),
            SalesWithoutTender: sales.Count(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)),
            SalesWithoutOutlet: sales.Count(sale => sale.Outlet is null),
            LinesWithoutValue: CountLinesWithoutValue(lines),
            UnseenDocumentCount: unseen.Sum(row => row.DocumentCount),
            ExpiredDocumentCount: unseen.Where(row => row.IsLostSale).Sum(row => row.DocumentCount),
            HeldSaleCount: held.Sum(row => row.SaleCount),
            OldestHeldAgeDays: oldestHeld,
            UnseenExposure: FoldExposure(unseen.SelectMany(row => row.Exposure)),
            HeldExposure: FoldExposure(held.SelectMany(row => row.Exposure)),
            TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(sales));
    }

    private static VanSalesExceptionsQualityResult BuildQuality(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        List<VanSalesUnseenResult> unseen,
        List<VanSalesHeldResult> held,
        List<VanSalesReceiptHandoverResult> handover,
        bool postingEnabled) =>
        new(
            UnseenDocumentCount: unseen.Sum(row => row.DocumentCount),
            ExpiredDocumentCount: unseen.Where(row => row.IsLostSale).Sum(row => row.DocumentCount),
            HeldSaleCount: held.Sum(row => row.SaleCount),
            HeldNeverAttemptedCount: held.Sum(row => row.NeverAttemptedCount),
            SalesWithoutTender: sales.Count(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)),
            SalesWithoutOutlet: sales.Count(sale => sale.Outlet is null),
            LinesWithoutValue: CountLinesWithoutValue(lines),
            ReceiptStatusesSeen: handover.Count,
            ReceiptsWithoutSignature: handover.Sum(row => row.WithoutSignature),
            PostingJobEnabled: postingEnabled);

    // ── Money ───────────────────────────────────────────────────────────────────

    private static List<VanSalesExposureResult> ExposureOf(
        IEnumerable<(string Currency, decimal Gross)> parts) =>
        parts
            .GroupBy(part => part.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesExposureResult(
                Currency: group.Key,
                DocumentCount: group.Count(),
                Gross: group.Sum(part => part.Gross)))
            .OrderByDescending(row => row.Gross)
            .ThenBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Rolls already-grouped exposure rows up a level without ever crossing currencies.</summary>
    private static List<VanSalesExposureResult> FoldExposure(IEnumerable<VanSalesExposureResult> parts) =>
        parts
            .GroupBy(part => part.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesExposureResult(
                Currency: group.Key,
                DocumentCount: group.Sum(part => part.DocumentCount),
                Gross: group.Sum(part => part.Gross)))
            .OrderByDescending(row => row.Gross)
            .ThenBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── Rows ────────────────────────────────────────────────────────────────────

    private sealed record RepRow(string Username, string? FullName);

    private sealed record UnseenRow(
        string Status,
        Guid? UserId,
        DateTime CapturedAt,
        decimal Gross,
        string Currency);

    private sealed record HeldRow(
        Guid? UserId,
        DateTime DocDate,
        decimal Gross,
        string Currency,
        int PostingAttempts,
        string? LastPostingError);
}
