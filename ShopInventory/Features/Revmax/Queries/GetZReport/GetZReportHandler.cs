using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetZReport;

public sealed class GetZReportHandler(
    IRevmaxClient revmaxClient,
    ILogger<GetZReportHandler> logger
) : IRequestHandler<GetZReportQuery, ErrorOr<ZReportResponse>>
{
    public async Task<ErrorOr<ZReportResponse>> Handle(
        GetZReportQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await revmaxClient.GetZReportAsync(cancellationToken);
            if (result is null)
                return Errors.Revmax.DeviceError("No response from device");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating Z-Report");
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
