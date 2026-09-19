using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakage;

public sealed record GetMarketBreakageQuery(int Id) : IRequest<ErrorOr<MarketBreakageDetailDto>>;
