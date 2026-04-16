using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetUnprocessedInvoicesSummary;

public sealed class GetUnprocessedInvoicesSummaryHandler(
    IRevmaxClient revmaxClient,
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
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting unprocessed invoices summary");
            return Errors.Revmax.DeviceError(ex.Message);
        }
    }
}
