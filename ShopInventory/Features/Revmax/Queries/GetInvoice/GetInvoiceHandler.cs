using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetInvoice;

public sealed class GetInvoiceHandler(
    IRevmaxClient revmaxClient,
    IAuditService auditService,
    ILogger<GetInvoiceHandler> logger
) : IRequestHandler<GetInvoiceQuery, ErrorOr<InvoiceResponse>>
{
    public async Task<ErrorOr<InvoiceResponse>> Handle(
        GetInvoiceQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await revmaxClient.GetInvoiceAsync(query.InvoiceNumber, cancellationToken);

            if (result == null || !result.Success)
            {
                var error = result?.Message ?? $"Invoice {query.InvoiceNumber} not found on REVMax.";

                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.ViewRevmaxInvoice,
                    RevmaxAudit.EntityType,
                    query.InvoiceNumber,
                    error,
                    false,
                    error);

                return Errors.Revmax.InvoiceNotFound(query.InvoiceNumber);
            }

            var details = result.Data?.ReceiptGlobalNo > 0
                ? $"Retrieved REVMax invoice {query.InvoiceNumber} with receipt #{result.Data.ReceiptGlobalNo}."
                : $"Retrieved REVMax invoice {query.InvoiceNumber}.";

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxInvoice,
                RevmaxAudit.EntityType,
                query.InvoiceNumber,
                details,
                true);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting invoice {InvoiceNumber}", query.InvoiceNumber);

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.ViewRevmaxInvoice,
                RevmaxAudit.EntityType,
                query.InvoiceNumber,
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
