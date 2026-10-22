using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

/// <summary>
/// Reads the vans for the period a Shop &amp; Vending review covers, and the period before it, and sets
/// them beside the review.
/// </summary>
/// <remarks>
/// Takes a review already read rather than reading one, because the email has one already: it reads the
/// review with its PDF, and reading it a second time for the figures would double the SAP margin read.
/// </remarks>
internal static class SalesPerformanceReader
{
    /// <summary>How many van items the email lists.</summary>
    public const int TopVanItems = 10;

    public static async Task<ErrorOr<SalesPerformance>> ForReviewAsync(
        IMediator mediator,
        ApplicationDbContext db,
        TaxSettings tax,
        ISAPServiceLayerClient sap,
        ILogger logger,
        DesktopSalesReview review,
        CancellationToken cancellationToken)
    {
        var vans = await mediator.Send(
            new GetVanSalesPerformanceReportQuery(review.FromDate, review.ToDate, TopItems: TopVanItems),
            cancellationToken);
        if (vans.IsError)
        {
            return vans.Errors;
        }

        var previous = await mediator.Send(
            new GetVanSalesPerformanceReportQuery(review.PreviousFromDate, review.PreviousToDate, TopItems: TopVanItems),
            cancellationToken);
        if (previous.IsError)
        {
            return previous.Errors;
        }

        var unconfirmed = await CountUnconfirmedAsync(db, review.FromDate, review.ToDate, cancellationToken);
        var partners = await VanPartnersAsync(db, tax, review, vans.Value, previous.Value, cancellationToken);

        var codes = review.Businesses
            .SelectMany(business => business.Currencies.SelectMany(currency => currency.ByPartner.Select(row => row.Key)))
            .Concat(partners.Current.Concat(partners.Previous).Select(fact => fact.VanAccountCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sapNames = await SapNamesAsync(sap, logger, codes, cancellationToken);

        return SalesPerformanceBuilder.Build(review, vans.Value, previous.Value, unconfirmed, partners, sapNames, DateTime.UtcNow);
    }

    /// <summary>
    /// Each partner's name as SAP holds it — "Factory Kefalos shop POS USD" for CIS006 — or null when SAP
    /// could not be asked.
    /// </summary>
    /// <remarks>
    /// The name on a sale is whatever the till or handset wrote, which is not always SAP's. The email
    /// still goes out without SAP: the figures are all local, and each partner then keeps its recorded
    /// name, which the email says.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, string>?> SapNamesAsync(
        ISAPServiceLayerClient sap, ILogger logger, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return (await sap.GetBusinessPartnersByCodesAsync(codes, cancellationToken))
                .Where(partner => !string.IsNullOrWhiteSpace(partner.CardCode) && !string.IsNullOrWhiteSpace(partner.CardName))
                .GroupBy(partner => partner.CardCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().CardName!.Trim(), StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the SAP names of {Count} business partner(s) for the sales performance; using the names on the sales", codes.Count);
            return null;
        }
    }

    /// <summary>
    /// The van sales of both periods through the same reader, with the same filter, as the van report —
    /// so the van accounts add up to its totals — and the names of the accounts they were invoiced to.
    /// </summary>
    private static async Task<VanPartnerFacts> VanPartnersAsync(
        ApplicationDbContext db,
        TaxSettings tax,
        DesktopSalesReview review,
        VanSalesPerformanceReportResult current,
        VanSalesPerformanceReportResult previous,
        CancellationToken cancellationToken)
    {
        var now = await VanSalesFactReader.LoadSalesAsync(db, tax, new VanSalesFactFilter(review.FromDate, review.ToDate), cancellationToken);
        var before = await VanSalesFactReader.LoadSalesAsync(db, tax, new VanSalesFactFilter(review.PreviousFromDate, review.PreviousToDate), cancellationToken);

        var accounts = now.Concat(before).Select(fact => fact.VanAccountCode).Distinct().ToList();

        // A van's account name is only ever recorded on its own sales; the API keeps no partner master.
        var offline = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales && accounts.Contains(sale.CardCode) && sale.CardName != null)
            .Select(sale => new { sale.CardCode, sale.CardName })
            .Distinct()
            .ToListAsync(cancellationToken);
        var online = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales && accounts.Contains(reservation.CardCode) && reservation.CardName != null)
            .Select(reservation => new { reservation.CardCode, reservation.CardName })
            .Distinct()
            .ToListAsync(cancellationToken);
        var names = offline.Concat(online)
            .Where(row => !string.IsNullOrWhiteSpace(row.CardName))
            .GroupBy(row => row.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(row => row.CardName!.Trim()).Order(StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);

        var reps = current.Reps.Concat(previous.Reps)
            .GroupBy(rep => rep.UserId)
            .ToDictionary(group => group.Key, group => group.First().DisplayName);

        // The reports' copy of SAP's booked cost: one read, the same every van report makes.
        var margin = await VanSalesMargin.LoadAsync(
            now, new StoredSaleInvoiceCostReader(db), null, cancellationToken);

        return new VanPartnerFacts(now, before, names, reps) { Margin = margin };
    }

    /// <summary>
    /// Van reservations made in the period that expired instead of confirming — the only trace an online
    /// van sale made during a SAP outage leaves, since the fact reader counts confirmed ones alone.
    /// </summary>
    private static Task<int> CountUnconfirmedAsync(ApplicationDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = VanSalesFacts.ToUtcWindow(from, to);
        return db.StockReservations
            .AsNoTracking()
            .CountAsync(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                       && reservation.Status == ReservationStatus.Expired
                                       && reservation.CreatedAt >= fromUtc
                                       && reservation.CreatedAt < toUtc
                                       && reservation.CreatedBy != null,
                cancellationToken);
    }
}
