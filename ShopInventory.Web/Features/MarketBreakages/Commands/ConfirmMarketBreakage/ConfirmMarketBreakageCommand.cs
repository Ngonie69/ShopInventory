using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.MarketBreakages.Commands.ConfirmMarketBreakage;

/// <summary>The office's count for every line of a report, and the go-ahead to transfer it to returns.</summary>
public sealed record ConfirmMarketBreakageCommand(
    int Id,
    IReadOnlyList<ConfirmMarketBreakageLineDto> Lines,
    string? Remarks) : IRequest<ErrorOr<MarketBreakageDecisionResultDto>>;
