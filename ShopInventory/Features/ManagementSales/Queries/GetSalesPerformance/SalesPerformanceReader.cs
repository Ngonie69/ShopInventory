using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Models.Entities;

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

        return SalesPerformanceBuilder.Build(review, vans.Value, previous.Value, unconfirmed, DateTime.UtcNow);
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
