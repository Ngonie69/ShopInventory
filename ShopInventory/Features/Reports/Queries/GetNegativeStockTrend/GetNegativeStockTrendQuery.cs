using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Reports.Queries.GetNegativeStockTrend;

/// <summary>
/// How much stock SAP has been holding below zero, day by day.
/// </summary>
/// <remarks>
/// The outcome measure for the negative-stock work. Every guard elsewhere stops a document that
/// would take stock under; none of them can say whether they worked. This can, and only by being
/// read repeatedly — one day's figure says nothing, a fortnight of them says everything.
/// </remarks>
public sealed record GetNegativeStockTrendQuery(int Days = 30) : IRequest<ErrorOr<NegativeStockTrendDto>>;

/// <param name="Days">The window asked for.</param>
/// <param name="Latest">The most recent day counted, or null if nothing has been counted yet.</param>
/// <param name="History">One entry per day counted, oldest first, so a trend reads left to right.</param>
/// <param name="WorstWarehouses">
/// The warehouses furthest below zero on the latest day. Where to look first, not a full list.
/// </param>
public sealed record NegativeStockTrendDto(
    int Days,
    NegativeStockDayDto? Latest,
    IReadOnlyList<NegativeStockDayDto> History,
    IReadOnlyList<NegativeStockWarehouseDto> WorstWarehouses);

/// <param name="ObservedOn">The day counted.</param>
/// <param name="Rows">Item/warehouse combinations below zero.</param>
/// <param name="Warehouses">How many warehouses those rows are spread across.</param>
/// <param name="UnitsBelowZero">
/// Total units below zero, as a positive number. The headline: rows say how widespread it is, units
/// say how bad.
/// </param>
public sealed record NegativeStockDayDto(
    DateTime ObservedOn,
    int Rows,
    int Warehouses,
    decimal UnitsBelowZero);

/// <param name="WarehouseCode">The warehouse.</param>
/// <param name="Rows">Items below zero in it.</param>
/// <param name="UnitsBelowZero">Total units below zero, as a positive number.</param>
public sealed record NegativeStockWarehouseDto(
    string WarehouseCode,
    int Rows,
    decimal UnitsBelowZero);
