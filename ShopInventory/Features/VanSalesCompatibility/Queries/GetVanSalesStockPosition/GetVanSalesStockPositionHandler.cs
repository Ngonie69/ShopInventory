using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesStockPosition;

/// <summary>
/// Rebuilds a van's current position from the morning count, the loads since, and the sales received.
/// </summary>
/// <remarks>
/// <para><b>Computed on read, deliberately.</b> Nothing decrements a van's snapshot during trading —
/// van sales only reach the stock ledger when they post to SAP at end of day — so the stored
/// <c>AvailableQuantity</c> is not the answer and making it the answer would mean moving where a van
/// sale commits, on a path where the money and the ZIMRA receipt are already settled. Doing the
/// arithmetic here changes nothing about how a sale is recorded, posted or reported, and gives the
/// same number.</para>
///
/// <para><b>Why these three terms.</b> <c>OriginalQuantity</c> is the handset's own opening count and
/// is the one figure on a van row that nothing else writes. Transfer adjustments are how a mid-day
/// load reaches this system. Sales are counted whether or not they have posted, because the question
/// is what is on the van, not what SAP has been told — which is the opposite of the choice the hourly
/// reconcile makes, and for the opposite reason.</para>
///
/// <para><b>What it is not.</b> Not authoritative over the handset, which knows about sales it has not
/// uploaded yet and is therefore ahead of this. It is the answer for a handset that has lost its
/// ledger — a reinstall, a replacement device, a handover — and a second opinion for a rep who wants
/// to check. It refuses nothing.</para>
/// </remarks>
public sealed class GetVanSalesStockPositionHandler(
    ApplicationDbContext db,
    ILogger<GetVanSalesStockPositionHandler> logger)
    : IRequestHandler<GetVanSalesStockPositionQuery, ErrorOr<VanSalesStockPositionResult>>
{
    public async Task<ErrorOr<VanSalesStockPositionResult>> Handle(
        GetVanSalesStockPositionQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

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
                "An assigned warehouse is required before a van can be asked what it is carrying.");
        }

        // The CAT calendar date, because that is how the post that files a van's count dates it. The
        // ledger day used elsewhere rolls at the 07:00 fetch instead, and asking for it here would
        // miss the van's own row every morning between midnight and seven.
        var tradingDate = AuditService.ToCAT(DateTime.UtcNow).Date;

        var snapshot = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(header => header.WarehouseCode == warehouseCode
                          && header.SnapshotDate == tradingDate)
            .Select(header => new { header.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null)
        {
            // No opening count, so there is no position to rebuild. Said plainly rather than answered
            // with an empty list, which a handset would render as a van carrying nothing — wrong, and
            // the kind of wrong that stops a day's selling.
            logger.LogInformation(
                "Van {WarehouseCode} was asked what it is carrying on {TradingDate:yyyy-MM-dd} but has "
                + "filed no opening count for the day",
                warehouseCode, tradingDate);

            return new VanSalesStockPositionResult
            {
                WarehouseCode = warehouseCode,
                TradingDate = tradingDate.ToString("yyyy-MM-dd"),
                Counted = false,
                LineCount = 0,
                Message = "This van has not filed an opening stock count for today, so its position "
                        + "is not known here. The handset's own count is the only record."
            };
        }

        var opening = await db.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.SnapshotId == snapshot.Id)
            .GroupBy(row => row.ItemCode)
            .Select(group => new
            {
                ItemCode = group.Key,
                Quantity = group.Sum(row => row.OriginalQuantity),
                Description = group.Max(row => row.ItemDescription)
            })
            .ToListAsync(cancellationToken);

        var transfers = await db.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate == tradingDate
                              && adjustment.WarehouseCode == warehouseCode)
            .GroupBy(adjustment => adjustment.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(a => a.AdjustmentQuantity) })
            .ToListAsync(cancellationToken);

        // The CAT trading day in UTC terms: CAT runs two hours ahead, so the day opens at 22:00 UTC
        // the evening before.
        var from = tradingDate.AddHours(-2);
        var to = from.AddDays(1);

        var sold = await db.DesktopSaleLines
            .AsNoTracking()
            .Where(line => line.WarehouseCode == warehouseCode
                        && line.Sale.CreatedAt >= from
                        && line.Sale.CreatedAt < to)
            .GroupBy(line => line.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken);

        var openingByItem = opening.ToDictionary(
            row => row.ItemCode, row => row.Quantity, StringComparer.OrdinalIgnoreCase);
        var descriptionByItem = opening
            .Where(row => row.Description is not null)
            .ToDictionary(row => row.ItemCode, row => row.Description, StringComparer.OrdinalIgnoreCase);
        var transferByItem = transfers.ToDictionary(
            row => row.ItemCode, row => row.Quantity, StringComparer.OrdinalIgnoreCase);
        var soldByItem = sold.ToDictionary(
            row => row.ItemCode, row => row.Quantity, StringComparer.OrdinalIgnoreCase);

        // Every item any of the three knows about. An item loaded mid-day has no opening row, and one
        // sold down to nothing still belongs on the list — a rep needs to see the zero rather than
        // find the item missing and assume the list is wrong.
        var itemCodes = openingByItem.Keys
            .Concat(transferByItem.Keys)
            .Concat(soldByItem.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase);

        var lines = new List<VanSalesStockPositionResultLine>();

        foreach (var itemCode in itemCodes)
        {
            var openingQuantity = openingByItem.GetValueOrDefault(itemCode);
            var transferred = transferByItem.GetValueOrDefault(itemCode);
            var soldQuantity = soldByItem.GetValueOrDefault(itemCode);

            lines.Add(new VanSalesStockPositionResultLine
            {
                Code = itemCode,
                Description = descriptionByItem.GetValueOrDefault(itemCode),
                OpeningQuantity = openingQuantity,
                TransferredQuantity = transferred,
                SoldQuantity = soldQuantity,

                // Floored: a van that has sold more than this system thinks it loaded is a real and
                // known condition — the end-of-day posting reports it as a shortfall — and a negative
                // here would read to a rep as stock owed rather than stock gone.
                Quantity = Math.Max(0m, openingQuantity + transferred - soldQuantity)
            });
        }

        return new VanSalesStockPositionResult
        {
            WarehouseCode = warehouseCode,
            TradingDate = tradingDate.ToString("yyyy-MM-dd"),
            Counted = true,
            LineCount = lines.Count,
            Lines = lines
        };
    }
}
