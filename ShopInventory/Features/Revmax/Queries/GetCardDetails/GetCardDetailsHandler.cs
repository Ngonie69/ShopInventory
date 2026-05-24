using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetCardDetails;

public sealed class GetCardDetailsHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
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
            {
                const string error = "No response from device";
                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.ViewRevmaxCardDetails,
                    RevmaxAudit.EntityType,
                    "CardDetails",
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = RevmaxAudit.IsSuccessCode(result.Code);
            var details = isSuccess
                ? $"Retrieved REVMax card details for device {result.DeviceSerialNumber ?? result.DeviceID ?? "unknown"}."
                : result.Message ?? "REVMax returned a non-success card details response.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxCardDetails,
                RevmaxAudit.EntityType,
                "CardDetails",
                details,
                isSuccess,
                isSuccess ? null : result.Message);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting card details");

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxCardDetails,
                RevmaxAudit.EntityType,
                "CardDetails",
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
