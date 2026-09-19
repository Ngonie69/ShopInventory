using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakages;

/// <summary>
/// Breakage reports for the office, newest first. <c>Status</c> is one of
/// <see cref="Models.Entities.MarketBreakageStatuses.All"/>, or <c>open</c> for everything still
/// waiting on the office (pending, failed, stranded), or empty for all.
/// </summary>
public sealed record GetMarketBreakagesQuery(
    string? Status,
    string? Search,
    int Page,
    int PageSize) : IRequest<ErrorOr<MarketBreakageListResponseDto>>;
