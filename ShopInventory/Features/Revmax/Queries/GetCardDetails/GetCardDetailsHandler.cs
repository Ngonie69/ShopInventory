using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetCardDetails;

public sealed class GetCardDetailsHandler(
    IRevmaxClient revmaxClient,
    ILogger<GetCardDetailsHandler> logger
) : IRequestHandler<GetCardDetailsQuery, ErrorOr<CardDetailsResponse>>
{
    public async Task<ErrorOr<CardDetailsResponse>> Handle(
        GetCardDetailsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await revmaxClient.GetCardDetailsAsync(cancellationToken);
            if (result is null)
                return Errors.Revmax.DeviceError("No response from device");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting card details");
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
