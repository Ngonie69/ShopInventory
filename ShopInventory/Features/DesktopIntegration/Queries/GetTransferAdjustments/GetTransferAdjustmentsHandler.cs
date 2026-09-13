using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferAdjustments;

/// <summary>
/// Reads the transfer adjustments the stock ledger holds for a warehouse.
/// </summary>
/// <remarks>
/// <para>
/// The local stock route already folds these into one <c>TransferAdjustment</c> figure per item per
/// day. That figure says how much moved but not which document moved it, when, or whether a transfer
/// SAP holds was ever applied at all — and transfers keyed straight into the SAP client reach the
/// ledger only if TransferEventListener's poll finds them and its webhook is delivered. A till auditing
/// its stock needs the rows to tell an applied transfer from a missed one.
/// </para>
/// <para>
/// Read from this API's own database; SAP is not asked.
/// </para>
/// </remarks>
public sealed class GetTransferAdjustmentsHandler(ApplicationDbContext context)
    : IRequestHandler<GetTransferAdjustmentsQuery, ErrorOr<List<TransferAdjustmentDto>>>
{
    /// <summary>A quarter, which is more than any till screen asks for.</summary>
    public const int MaxDays = 92;

    public async Task<ErrorOr<List<TransferAdjustmentDto>>> Handle(
        GetTransferAdjustmentsQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (from > to)
            return Errors.DesktopIntegration.ValidationFailed("fromDate must be before or equal to toDate");

        if ((to - from).TotalDays + 1 > MaxDays)
            return Errors.DesktopIntegration.ValidationFailed($"A range can cover at most {MaxDays} days");

        var rows = await context.StockTransferAdjustments
            .AsNoTracking()
            .Where(a => a.WarehouseCode == query.WarehouseCode && a.SnapshotDate >= from && a.SnapshotDate <= to)
            .OrderBy(a => a.DetectedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        return rows
            .Select(a => new TransferAdjustmentDto(
                a.SnapshotDate,
                a.ItemCode,
                a.WarehouseCode,
                a.AdjustmentQuantity,
                a.Direction,
                a.TransferDocEntry,
                a.TransferDocNum,
                a.SourceWarehouse,
                a.DestinationWarehouse,
                // Written from DateTime.UtcNow and read back from a column that keeps no kind.
                DateTime.SpecifyKind(a.DetectedAt, DateTimeKind.Utc)))
            .ToList();
    }
}
