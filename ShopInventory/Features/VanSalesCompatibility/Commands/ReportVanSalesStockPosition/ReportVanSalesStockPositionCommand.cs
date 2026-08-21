using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ReportVanSalesStockPosition;

public sealed record ReportVanSalesStockPositionCommand(
    VanSalesStockPositionRequest Request,
    Guid UserId) : IRequest<ErrorOr<VanSalesStockPositionResponse>>;
