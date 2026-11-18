using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Stock.Queries.GetStockMovementTotals;

/// <summary>
/// The stock ledger's movements in a warehouse between two moments, totalled per item and by what
/// moved them. Answers "how fast does this sell" without shipping every sale line.
/// </summary>
public sealed record GetStockMovementTotalsQuery(string WarehouseCode, DateTime FromUtc, DateTime ToUtc)
    : IRequest<ErrorOr<StockMovementTotalsResult>>;

public sealed record StockMovementTotalsResult(
    string WarehouseCode,
    DateTime FromUtc,
    DateTime ToUtc,
    List<StockMovementTotalDto> Items);

/// <param name="Sold">Units sales took, net of what was handed back (Commit, Settle and Release).</param>
/// <param name="TransferredIn">Units stock transfers brought in.</param>
/// <param name="TransferredOut">Units stock transfers took out, as a positive figure.</param>
/// <param name="Corrected">The hourly SAP corrections' net, signed.</param>
public sealed record StockMovementTotalDto(
    string ItemCode,
    decimal Sold,
    decimal TransferredIn,
    decimal TransferredOut,
    decimal Corrected,
    int Movements);
