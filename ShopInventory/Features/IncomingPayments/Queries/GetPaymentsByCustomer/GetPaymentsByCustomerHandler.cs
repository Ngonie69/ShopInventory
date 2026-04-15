using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByCustomer;

public sealed class GetPaymentsByCustomerHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPaymentsByCustomerHandler> logger
) : IRequestHandler<GetPaymentsByCustomerQuery, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        GetPaymentsByCustomerQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.CardCode))
            return Errors.IncomingPayment.CustomerCodeRequired;

        try
        {
            var payments = await sapClient.GetIncomingPaymentsByCustomerAsync(request.CardCode, cancellationToken);

            logger.LogInformation("Retrieved {Count} incoming payments for customer {CardCode}", payments.Count, request.CardCode);

            return new
            {
                CardCode = request.CardCode,
                Count = payments.Count,
                Payments = payments.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.IncomingPayment.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving customer payments");
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving payments for customer {CardCode}", request.CardCode);
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }
}
