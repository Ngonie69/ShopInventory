using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetUnprocessedInvoicesSummary;

public sealed class GetUnprocessedInvoicesSummaryHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
    ILogger<GetUnprocessedInvoicesSummaryHandler> logger
) : IRequestHandler<GetUnprocessedInvoicesSummaryQuery, ErrorOr<UnprocessedInvoicesSummaryResponse>>
{
    public async Task<ErrorOr<UnprocessedInvoicesSummaryResponse>> Handle(
        GetUnprocessedInvoicesSummaryQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await revmaxClient.GetUnprocessedInvoicesSummaryAsync(cancellationToken);
            if (result is null)
            {
                const string error = "No response from device";
                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.ViewRevmaxUnprocessedInvoices,
                    RevmaxAudit.EntityType,
                    "UnprocessedInvoices",
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = RevmaxAudit.IsSuccessCode(result.Code);
            var details = isSuccess
                ? $"Retrieved {result.Data?.Count ?? 0} unprocessed REVMax invoices."
                : result.Message ?? "REVMax returned a non-success unprocessed invoices response.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxUnprocessedInvoices,
                RevmaxAudit.EntityType,
                "UnprocessedInvoices",
                details,
                isSuccess,
                isSuccess ? null : result.Message);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting unprocessed invoices summary");

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxUnprocessedInvoices,
                RevmaxAudit.EntityType,
                "UnprocessedInvoices",
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
