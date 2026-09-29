using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ReportVanSalesStockPosition;

/// <summary>
/// Takes a van's own count of what it is carrying, and no longer files it as the van's opening stock.
/// </summary>
/// <remarks>
/// <para><b>What it used to do.</b> The count became the van's <c>DailyStockSnapshot</c> for the day if it
/// arrived before the 07:00 read from SAP, which then skipped the van. That was built when SAP learnt of
/// van sales only at the end-of-day run, so SAP's morning figure for a van was a day behind. It no longer
/// is: online sales post within minutes and offline ones every half hour, so by 07:00 SAP has the whole
/// of the previous day.</para>
///
/// <para><b>Why it had to stop.</b> The handset sends its ledger as it stands, without reading anything
/// first, and the count was filed under the account's first warehouse as the server sees it now. Nothing
/// checked that the two were the same van. On 2026-09-29 VAN004's opening position was VAN005's stock —
/// 30 of YOG020 and 10 of YOG023 where SAP held none — and Local stock showed them all day, because vans
/// are left out of the hourly correction. A correctly assigned handset was not safe either: its ledger
/// could be a day's sales low. The van stock report meanwhile reads <c>OriginalQuantity</c> as SAP's own
/// book stock and reconciles it against SAP documents, which a handset's figure is not.</para>
///
/// <para><b>What it does now.</b> Answers <c>accepted</c> — so a handset marks the day filed and stops
/// resending, as every build in the field does on that answer — writes nothing, and logs the count. A
/// van's day opens on the 07:00 read from SAP and on nothing else.</para>
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

        // Still refused, so the contract a handset was built against does not move: an empty count is
        // as likely to be a ledger that failed to load as a van carrying nothing.
        var lineCount = request.Lines.Count(line => !string.IsNullOrWhiteSpace(line.Code));
        if (lineCount == 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.EmptyStockPosition",
                "A stock position must list what the van is carrying. An empty count would report the " +
                "van as loaded with nothing.");
        }

        var capturedUtc = CaptureClock.Resolve(CaptureClock.Parse(request.CapturedAt));
        var tradingDate = AuditService.ToCAT(capturedUtc).Date;

        logger.LogInformation(
            "Van {WarehouseCode} reported a count of {LineCount} line(s) for {TradingDate:yyyy-MM-dd}. "
            + "It is not filed as the opening position; the day opens on the 07:00 read from SAP.",
            warehouseCode,
            lineCount,
            tradingDate);

        return new VanSalesStockPositionResponse
        {
            Accepted = true,
            Duplicate = false,
            WarehouseCode = warehouseCode,
            TradingDate = tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LineCount = lineCount,
            Message = "Received. The van's opening position for the day comes from SAP, not from this count."
        };
    }
}
