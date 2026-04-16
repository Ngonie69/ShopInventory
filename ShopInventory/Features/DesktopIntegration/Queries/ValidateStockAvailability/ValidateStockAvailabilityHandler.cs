using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.ValidateStockAvailability;

public sealed class ValidateStockAvailabilityHandler(
    IStockReservationService reservationService
) : IRequestHandler<ValidateStockAvailabilityQuery, ErrorOr<StockValidationResultDto>>
{
    public async Task<ErrorOr<StockValidationResultDto>> Handle(
        ValidateStockAvailabilityQuery query,
        CancellationToken cancellationToken)
    {
        var (isValid, errors) = await reservationService.ValidateStockAvailabilityAsync(
            query.Request.Lines, query.Request.ExcludeReservationId, cancellationToken);

        return new StockValidationResultDto
        {
            IsValid = isValid,
            Errors = errors,
            Message = isValid ? "Stock is available" : "Some items have insufficient stock"
        };
    }
}
