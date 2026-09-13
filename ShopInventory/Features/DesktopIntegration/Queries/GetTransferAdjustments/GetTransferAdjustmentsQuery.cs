using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferAdjustments;

/// <summary>
/// The transfers the stock ledger applied to a warehouse over a run of snapshot days, one row per
/// transfer line as TransferEventListener reported it.
/// </summary>
/// <param name="FromDate">First snapshot day, inclusive.</param>
/// <param name="ToDate">Last snapshot day, inclusive.</param>
public sealed record GetTransferAdjustmentsQuery(
    string WarehouseCode,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<List<TransferAdjustmentDto>>>;

/// <param name="SnapshotDate">The snapshot day whose ledger the transfer moved — the day in force when it was detected, not the transfer's document date.</param>
/// <param name="Quantity">Signed: positive arrived in the warehouse, negative left it.</param>
/// <param name="Direction">IN or OUT, from the warehouse's side.</param>
/// <param name="DetectedAt">When the platform applied it, in UTC.</param>
public sealed record TransferAdjustmentDto(
    DateTime SnapshotDate,
    string ItemCode,
    string WarehouseCode,
    decimal Quantity,
    string Direction,
    int? TransferDocEntry,
    int? TransferDocNum,
    string? SourceWarehouse,
    string? DestinationWarehouse,
    DateTime DetectedAt);
