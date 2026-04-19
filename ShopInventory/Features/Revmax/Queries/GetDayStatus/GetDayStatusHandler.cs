using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetDayStatus;

public sealed class GetDayStatusHandler(
    IRevmaxClient revmaxClient,
    ILogger<GetDayStatusHandler> logger
) : IRequestHandler<GetDayStatusQuery, ErrorOr<DayStatusResponse>>
{
    public async Task<ErrorOr<DayStatusResponse>> Handle(
        GetDayStatusQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await revmaxClient.GetDayStatusAsync(cancellationToken);
            if (result is null)
                return Errors.Revmax.DeviceError("No response from device");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting day status");
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
