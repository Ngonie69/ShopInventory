using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ReportVanSalesStockPosition;

/// <summary>
/// Files a van's own count of what it is carrying as that van's stock snapshot for the trading day.
/// </summary>
/// <remarks>
/// <para><b>Why the handset is the right author for a van.</b> <c>DailyStockSnapshotJob</c> reads SAP,
/// which is the correct source for a depot and the wrong one for a van: a van's stock is decremented by
/// sales signed on the handset and uploaded hours later, so SAP's figure for a van warehouse is a day
/// behind and the job faithfully snapshots the staleness. Worse, the job only visits the warehouses
/// named in <c>DailyStockSettings.MonitoredWarehouses</c>, so a van absent from that list produced no
/// snapshot at all — and the van stock report, which reads nothing else, showed an empty page rather
/// than an error.</para>
///
/// <para><b>The first count of the day wins.</b> A second post for the same van and day is answered as
/// a duplicate and changes nothing. The report's whole method is to compare a morning's opening
/// position against the previous morning's less what sold in between, so an opening figure that can be
/// rewritten later in the day is not an opening figure — it would silently absorb exactly the variance
/// the report exists to surface. A handset that loses the reply re-sends, and is told the count is
/// already held.</para>
///
/// <para>Written as a <see cref="DailyStockSnapshotEntity"/> rather than a table of its own so the
/// portal's van stock report needs no second read path. <c>OriginalQuantity</c> and
/// <c>AvailableQuantity</c> are both set to the counted figure: the first is the morning position the
/// report reads, and the second is the working quantity the desktop paths decrement — nothing
/// decrements it for a van, which is precisely why the report reads the first.</para>
/// </remarks>
public sealed class ReportVanSalesStockPositionHandler(
    ApplicationDbContext db,
    ILogger<ReportVanSalesStockPositionHandler> logger)
    : IRequestHandler<ReportVanSalesStockPositionCommand, ErrorOr<VanSalesStockPositionResponse>>
{
    public async Task<ErrorOr<VanSalesStockPositionResponse>> Handle(
        ReportVanSalesStockPositionCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized(
                "VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required before a van can report what it is carrying.");
        }

        // An empty count is refused rather than filed. A van genuinely carrying nothing is not the case
        // this would usually be: a handset whose ledger failed to load reports exactly the same thing,
        // and an empty opening position would report the day's entire load as a variance.
        if (request.Lines.Count == 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.EmptyStockPosition",
                "A stock position must list what the van is carrying. An empty count would report the " +
                "van as loaded with nothing.");
        }

        var capturedUtc = CaptureClock.Resolve(CaptureClock.Parse(request.CapturedAt));
        var tradingDate = AuditService.ToCAT(capturedUtc).Date;

        var existing = await db.DailyStockSnapshots
            .FirstOrDefaultAsync(
                snapshot => snapshot.WarehouseCode == warehouseCode
                            && snapshot.SnapshotDate == tradingDate,
                cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Van {WarehouseCode} re-sent its stock position for {TradingDate:yyyy-MM-dd}; the held " +
                "count of {ItemCount} row(s) is kept.",
                warehouseCode,
                tradingDate,
                existing.ItemCount);

            return Held(warehouseCode, tradingDate, existing.ItemCount);
        }

        var items = request.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Code))
            .Select(line => new DailyStockSnapshotItemEntity
            {
                ItemCode = line.Code.Trim(),
                ItemDescription = line.Description,
                WarehouseCode = warehouseCode,
                BatchNumber = string.IsNullOrWhiteSpace(line.Batch) ? null : line.Batch.Trim(),
                OriginalQuantity = line.Quantity,
                AvailableQuantity = line.Quantity,
                ExpiryDate = ParseDate(line.ExpiryDate)
            })
            .ToList();

        if (items.Count == 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.EmptyStockPosition",
                "Every line in this stock position is missing its item code.");
        }

        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = tradingDate,
            WarehouseCode = warehouseCode,
            // Complete, because the van has finished counting — there is no second pass to wait for the
            // way there is when the job is walking SAP a page at a time. The report reads only complete
            // snapshots, so anything else would file the count and then not show it.
            Status = StockSnapshotStatus.Complete,
            ItemCount = items.Count,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            Items = items
        };

        db.DailyStockSnapshots.Add(snapshot);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A failed save is only a duplicate if the day now actually has a position. Asking rather
            // than assuming, because the two outcomes need opposite answers: a race against the
            // snapshot job is a success the handset should stop retrying, while any other failure —
            // and this catch would otherwise swallow every one of them — must come back as an error.
            // Reporting that as "accepted, duplicate" would lose the van's only count of the day and
            // tell the handset to discard its own copy.
            db.ChangeTracker.Clear();

            var held = await db.DailyStockSnapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    snapshot => snapshot.WarehouseCode == warehouseCode
                                && snapshot.SnapshotDate == tradingDate,
                    cancellationToken);

            if (held is not null)
            {
                logger.LogInformation(
                    "Van {WarehouseCode} raced another writer for {TradingDate:yyyy-MM-dd}; the held count is kept.",
                    warehouseCode,
                    tradingDate);

                return Held(warehouseCode, tradingDate, held.ItemCount);
            }

            logger.LogError(
                ex,
                "Van {WarehouseCode} reported a stock position for {TradingDate:yyyy-MM-dd} that could not " +
                "be stored, and no position is held for that day. The handset still has the count.",
                warehouseCode,
                tradingDate);

            return Error.Failure(
                "VanSalesCompatibility.StockPositionNotStored",
                "This van's stock position could not be stored. Try again when there is signal.");
        }

        logger.LogInformation(
            "Van {WarehouseCode} reported {ItemCount} stock row(s) for {TradingDate:yyyy-MM-dd}.",
            warehouseCode,
            items.Count,
            tradingDate);

        return new VanSalesStockPositionResponse
        {
            Accepted = true,
            Duplicate = false,
            WarehouseCode = warehouseCode,
            TradingDate = Format(tradingDate),
            LineCount = items.Count
        };
    }

    private static VanSalesStockPositionResponse Held(
        string warehouseCode,
        DateTime tradingDate,
        int lineCount) => new()
        {
            Accepted = true,
            Duplicate = true,
            WarehouseCode = warehouseCode,
            TradingDate = Format(tradingDate),
            LineCount = lineCount,
            Message = "This van's opening position for the day was already held."
        };

    private static string Format(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A date the handset sent, or null. Unparseable reads as "no expiry recorded" rather than as a
    /// date: the expiry view lists what is close to going off, and a guessed date puts a good batch on
    /// that list or keeps a bad one off it.
    /// </summary>
    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : null;
}
