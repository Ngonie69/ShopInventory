using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Commands.SetLicense;

public sealed class SetLicenseHandler(
    IRevmaxClient revmaxClient,
    ILogger<SetLicenseHandler> logger
) : IRequestHandler<SetLicenseCommand, ErrorOr<LicenseResponse>>
{
    public async Task<ErrorOr<LicenseResponse>> Handle(
        SetLicenseCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.License))
        {
            return Errors.Revmax.InvalidLicense;
        }

        try
        {
            var result = await revmaxClient.SetLicenseAsync(command.License, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting license");
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
