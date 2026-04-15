using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentByDocNum;

public sealed class GetPaymentByDocNumHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPaymentByDocNumHandler> logger
) : IRequestHandler<GetPaymentByDocNumQuery, ErrorOr<IncomingPaymentDto>>
{
    public async Task<ErrorOr<IncomingPaymentDto>> Handle(
        GetPaymentByDocNumQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        try
        {
            var payment = await sapClient.GetIncomingPaymentByDocNumAsync(request.DocNum, cancellationToken);
            if (payment is null)
                return Errors.IncomingPayment.NotFoundByDocNum(request.DocNum);

            logger.LogInformation("Retrieved incoming payment by DocNum: {DocNum}, DocEntry: {DocEntry}", payment.DocNum, payment.DocEntry);
            return payment.ToDto();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.IncomingPayment.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving incoming payment DocNum {DocNum}", request.DocNum);
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving incoming payment by DocNum {DocNum}", request.DocNum);
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }
}
