using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetDayStatus;

public sealed class GetDayStatusHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
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
            {
                const string error = "No response from device";
                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.ViewRevmaxDayStatus,
                    RevmaxAudit.EntityType,
                    "DayStatus",
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = RevmaxAudit.IsSuccessCode(result.Code);
            var entityId = !string.IsNullOrWhiteSpace(result.FiscalDay)
                ? result.FiscalDay
                : result.Data?.LastFiscalDayNo > 0
                    ? result.Data.LastFiscalDayNo.ToString()
                    : "DayStatus";
            var details = isSuccess
                ? $"Retrieved REVMax day status {(result.Data?.FiscalDayStatus ?? "unknown")} for fiscal day {entityId}."
                : result.Message ?? "REVMax returned a non-success day status response.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxDayStatus,
                RevmaxAudit.EntityType,
                entityId,
                details,
                isSuccess,
                isSuccess ? null : result.Message);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting day status");

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxDayStatus,
                RevmaxAudit.EntityType,
                "DayStatus",
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
