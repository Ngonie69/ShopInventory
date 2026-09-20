using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffReasons;

/// <summary>
/// The reasons a write-off may be given, and whether SAP itself will record the one chosen.
/// </summary>
public sealed record GetStockWriteOffReasonsQuery : IRequest<ErrorOr<StockWriteOffReasonsResponseDto>>;
