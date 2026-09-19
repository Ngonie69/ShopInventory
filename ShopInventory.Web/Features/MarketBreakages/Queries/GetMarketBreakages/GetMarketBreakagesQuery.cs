using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakages;

public sealed record GetMarketBreakagesQuery(string? Status, string? Search, int Page, int PageSize)
    : IRequest<ErrorOr<MarketBreakageListResponseDto>>;
