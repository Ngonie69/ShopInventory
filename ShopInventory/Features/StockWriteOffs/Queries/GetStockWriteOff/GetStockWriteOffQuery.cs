using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOff;

/// <summary>One write-off with its lines and what happened to it.</summary>
public sealed record GetStockWriteOffQuery(int WriteOffId) : IRequest<ErrorOr<StockWriteOffDetailDto>>;
