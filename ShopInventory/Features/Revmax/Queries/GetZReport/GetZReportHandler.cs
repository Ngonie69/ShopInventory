using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetZReport;

public sealed class GetZReportHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
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
            {
                const string error = "No response from device";
                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.GenerateRevmaxZReport,
                    RevmaxAudit.EntityType,
                    "ZReport",
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = RevmaxAudit.IsSuccessCode(result.Code);
            var entityId = string.IsNullOrWhiteSpace(result.FiscalDay) ? "ZReport" : result.FiscalDay;
            var details = isSuccess
                ? $"Generated REVMax Z-report for fiscal day {entityId}."
                : result.Message ?? "REVMax returned a non-success Z-report response.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.GenerateRevmaxZReport,
                RevmaxAudit.EntityType,
                entityId,
                details,
                isSuccess,
                isSuccess ? null : result.Message);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating Z-Report");

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.GenerateRevmaxZReport,
                RevmaxAudit.EntityType,
                "ZReport",
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
