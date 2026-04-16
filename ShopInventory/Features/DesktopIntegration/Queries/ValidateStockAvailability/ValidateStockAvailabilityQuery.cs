using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.ValidateStockAvailability;

public sealed record ValidateStockAvailabilityQuery(
    ValidateStockRequest Request
) : IRequest<ErrorOr<StockValidationResultDto>>;
