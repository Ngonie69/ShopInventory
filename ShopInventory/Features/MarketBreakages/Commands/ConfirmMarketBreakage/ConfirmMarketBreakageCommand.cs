using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Commands.ConfirmMarketBreakage;

public sealed record ConfirmMarketBreakageCommand(
    int BreakageId,
    IReadOnlyList<ConfirmMarketBreakageLineDto> Lines,
    string? Remarks,
    Guid UserId) : IRequest<ErrorOr<MarketBreakageDecisionResultDto>>;
