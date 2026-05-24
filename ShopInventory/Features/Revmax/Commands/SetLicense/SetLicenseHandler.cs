using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Commands.SetLicense;

public sealed class SetLicenseHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
    ILogger<SetLicenseHandler> logger
) : IRequestHandler<SetLicenseCommand, ErrorOr<LicenseResponse>>
{
    public async Task<ErrorOr<LicenseResponse>> Handle(
        SetLicenseCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.License))
        {
            const string error = "License key is required.";
            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.UpdateRevmaxLicense,
                RevmaxAudit.EntityType,
                "License",
                error,
                false,
                error);
            return Errors.Revmax.InvalidLicense;
        }

        try
        {
            var result = await revmaxClient.SetLicenseAsync(command.License, cancellationToken);
            if (result is null)
            {
                const string error = "No response from device";
                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.UpdateRevmaxLicense,
                    RevmaxAudit.EntityType,
                    "License",
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = RevmaxAudit.IsSuccessCode(result.Code);
            var details = isSuccess
                ? $"Updated REVMax license for device {result.DeviceSerialNumber ?? result.DeviceID ?? "unknown"}."
                : result.Message ?? "REVMax rejected the license update.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.UpdateRevmaxLicense,
                RevmaxAudit.EntityType,
                "License",
                details,
                isSuccess,
                isSuccess ? null : result.Message);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting license");

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.UpdateRevmaxLicense,
                RevmaxAudit.EntityType,
                "License",
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
