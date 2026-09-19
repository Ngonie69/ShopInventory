using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Commands.ReportMarketBreakage;

public sealed record ReportMarketBreakageCommand(
    VanSalesMarketBreakageRequest Request,
    Guid UserId) : IRequest<ErrorOr<VanSalesMarketBreakageResponse>>;
