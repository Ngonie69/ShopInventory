using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentByDocEntry;

public sealed class GetPaymentByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPaymentByDocEntryHandler> logger
) : IRequestHandler<GetPaymentByDocEntryQuery, ErrorOr<IncomingPaymentDto>>
{
    public async Task<ErrorOr<IncomingPaymentDto>> Handle(
        GetPaymentByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        try
        {
            var payment = await sapClient.GetIncomingPaymentByDocEntryAsync(request.DocEntry, cancellationToken);
            if (payment is null)
                return Errors.IncomingPayment.NotFound(request.DocEntry);

            logger.LogInformation("Retrieved incoming payment DocEntry: {DocEntry}, DocNum: {DocNum}", payment.DocEntry, payment.DocNum);
            return payment.ToDto();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.IncomingPayment.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving incoming payment {DocEntry}", request.DocEntry);
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving incoming payment {DocEntry}", request.DocEntry);
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }
}
