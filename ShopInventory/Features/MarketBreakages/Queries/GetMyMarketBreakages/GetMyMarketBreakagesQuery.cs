using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMyMarketBreakages;

/// <summary>The caller's own breakage reports from the last <see cref="Days"/> days, newest first.</summary>
public sealed record GetMyMarketBreakagesQuery(Guid UserId, int Days = 30) : IRequest<ErrorOr<List<MarketBreakageDetailDto>>>;
