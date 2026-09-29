using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Stock;
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
/// <para><b>Why these three terms.</b> <c>OriginalQuantity</c> is SAP's book stock at the 07:00 read,
/// the one figure on a van row nothing moves during the day. Transfer adjustments are how a mid-day
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

        // The CAT calendar date. The 07:00 read files the van's row under the ledger day, which is the
        // same date from then on; before seven there is no row for today, and the answer is "unknown".
        var tradingDate = AuditService.ToCAT(DateTime.UtcNow).Date;

        var position = await VanStockPosition.ReadAsync(
            db, warehouseCode, tradingDate, leaveOutSalesStillHeld: false, cancellationToken);

        if (!position.Counted)
        {
            // No opening count, so there is no position to rebuild. Said plainly rather than answered
            // with an empty list, which a handset would render as a van carrying nothing — wrong, and
            // the kind of wrong that stops a day's selling.
            logger.LogInformation(
                "Van {WarehouseCode} was asked what it is carrying on {TradingDate:yyyy-MM-dd} but has "
                + "no opening stock for the day yet",
                warehouseCode, tradingDate);

            return new VanSalesStockPositionResult
            {
                WarehouseCode = warehouseCode,
                TradingDate = tradingDate.ToString("yyyy-MM-dd"),
                Counted = false,
                LineCount = 0,
                Message = "This van has no opening stock for today yet — it is read from SAP at 07:00 — so "
                        + "its position is not known here. The handset's own count is the only record."
            };
        }

        var lines = position.Items
            .Select(item => new VanSalesStockPositionResultLine
            {
                Code = item.ItemCode,
                Description = item.Description,
                OpeningQuantity = item.Opening,
                TransferredQuantity = item.Transferred,
                SoldQuantity = item.Sold,
                Quantity = item.Quantity
            })
            .ToList();

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
