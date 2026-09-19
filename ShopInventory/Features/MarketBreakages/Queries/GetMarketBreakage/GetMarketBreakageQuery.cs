using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakage;

public sealed record GetMarketBreakageQuery(int BreakageId) : IRequest<ErrorOr<MarketBreakageDetailDto>>;
