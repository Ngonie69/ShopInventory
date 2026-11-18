using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Stock.Queries.GetStockAsAt;

/// <summary>
/// What a warehouse held at one moment, per item, rebuilt from the app's own stock ledger.
/// </summary>
/// <remarks>
/// SAP's warehouse journal (OINM) is not readable through the Service Layer's SQLQueries, so a past
/// on-hand figure cannot come from SAP. The ledger can answer it for every warehouse the morning
/// snapshot covers: the snapshot is SAP's in-stock figure at the fetch, and every movement after it
/// is journalled with the balance it left (<see cref="Models.Entities.StockMovementEntity"/>).
/// </remarks>
public sealed record GetStockAsAtQuery(string WarehouseCode, DateTime AsAtUtc)
    : IRequest<ErrorOr<StockAsAtResult>>;

/// <param name="Recorded">
/// False when no finished snapshot covers the moment — before the ledger began, or for a warehouse
/// the morning fetch does not cover. <see cref="Items"/> is then empty.
/// </param>
/// <param name="LedgerDay">The snapshot day the moment belongs to.</param>
/// <param name="SnapshotTakenAtUtc">When that day's opening figures were read from SAP.</param>
/// <param name="RecordedFrom">The first ledger day this warehouse can be read for, if any.</param>
public sealed record StockAsAtResult(
    string WarehouseCode,
    DateTime AsAtUtc,
    bool Recorded,
    DateTime? LedgerDay,
    DateTime? SnapshotTakenAtUtc,
    DateTime? RecordedFrom,
    List<StockAsAtItemDto> Items);

/// <param name="OnHand">What the item held in the warehouse at the moment, across every batch.</param>
/// <param name="OpeningQuantity">The day's opening figure from SAP, before any movement.</param>
/// <param name="LastMovementAtUtc">The latest journalled movement at or before the moment, that day.</param>
public sealed record StockAsAtItemDto(
    string ItemCode,
    string? ItemName,
    decimal OnHand,
    decimal OpeningQuantity,
    DateTime? LastMovementAtUtc);
