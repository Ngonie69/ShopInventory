using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetLicense;

public sealed class GetLicenseHandler(
    IRevmaxClient revmaxClient,
    ILogger<GetLicenseHandler> logger
) : IRequestHandler<GetLicenseQuery, ErrorOr<LicenseResponse>>
{
    public async Task<ErrorOr<LicenseResponse>> Handle(
        GetLicenseQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await revmaxClient.GetLicenseAsync(cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting license");
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
