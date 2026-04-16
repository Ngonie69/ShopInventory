using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetInvoice;

public sealed class GetInvoiceHandler(
    IRevmaxClient revmaxClient,
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
                return Errors.Revmax.InvoiceNotFound(query.InvoiceNumber);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting invoice {InvoiceNumber}", query.InvoiceNumber);
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
