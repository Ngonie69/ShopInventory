using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.MarketBreakages.Commands.RejectMarketBreakage;

public sealed record RejectMarketBreakageCommand(int Id, string Remarks) : IRequest<ErrorOr<MarketBreakageDecisionResultDto>>;
