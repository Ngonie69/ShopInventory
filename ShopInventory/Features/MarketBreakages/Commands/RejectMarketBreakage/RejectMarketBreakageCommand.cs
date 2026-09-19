using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Commands.RejectMarketBreakage;

public sealed record RejectMarketBreakageCommand(
    int BreakageId,
    string? Remarks,
    Guid UserId) : IRequest<ErrorOr<MarketBreakageDecisionResultDto>>;
