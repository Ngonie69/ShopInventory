using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Stock.Queries.GetStockMovements;

/// <summary>
/// The stock ledger's movements for a warehouse between two moments, optionally for one item.
/// </summary>
public sealed record GetStockMovementsQuery(
    string WarehouseCode,
    DateTime FromUtc,
    DateTime ToUtc,
    string? ItemCode = null
) : IRequest<ErrorOr<StockMovementsResult>>;

/// <param name="WarehouseCode">The warehouse the movements belong to.</param>
/// <param name="FromUtc">Start of the window, in UTC, exclusive.</param>
/// <param name="ToUtc">End of the window, in UTC, inclusive.</param>
/// <param name="Truncated">More movements fell in the window than <see cref="Movements"/> carries.</param>
/// <param name="Movements">The movements in the window, oldest first, at most 2000.</param>
/// <param name="Openings">
/// Each morning's opening figure inside the window, for the one item asked for. A ledger day starts
/// from SAP's figure, not from the previous day's close, so a running balance across days has to
/// restart there. Empty when no item is named.
/// </param>
public sealed record StockMovementsResult(
    string WarehouseCode,
    DateTime FromUtc,
    DateTime ToUtc,
    bool Truncated,
    List<StockMovementDto> Movements,
    List<StockDayOpeningDto> Openings);

public sealed record StockMovementDto(
    long Id,
    DateTime OccurredAtUtc,
    DateTime LedgerDay,
    string Kind,
    string ItemCode,
    decimal Quantity,
    decimal BalanceAfter,
    string Reference,
    string? DocumentKey);

public sealed record StockDayOpeningDto(DateTime LedgerDay, DateTime TakenAtUtc, decimal Quantity);
