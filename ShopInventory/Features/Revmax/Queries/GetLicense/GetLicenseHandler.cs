using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetLicense;

public sealed class GetLicenseHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
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
            if (result is null)
            {
                const string error = "No response from device";
                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.ViewRevmaxLicense,
                    RevmaxAudit.EntityType,
                    "License",
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = RevmaxAudit.IsSuccessCode(result.Code);
            var details = isSuccess
                ? $"Retrieved REVMax license for device {result.DeviceSerialNumber ?? result.DeviceID ?? "unknown"}."
                : result.Message ?? "REVMax returned a non-success license response.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxLicense,
                RevmaxAudit.EntityType,
                "License",
                details,
                isSuccess,
                isSuccess ? null : result.Message);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting license");

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxLicense,
                RevmaxAudit.EntityType,
                "License",
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
