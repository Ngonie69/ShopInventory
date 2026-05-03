using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByCustomer;

public sealed class GetPaymentsByCustomerHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPaymentsByCustomerHandler> logger
) : IRequestHandler<GetPaymentsByCustomerQuery, ErrorOr<IncomingPaymentDateResponseDto>>
{
    public async Task<ErrorOr<IncomingPaymentDateResponseDto>> Handle(
        GetPaymentsByCustomerQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.CardCode))
            return Errors.IncomingPayment.CustomerCodeRequired;

        if (request.FromDate.HasValue && request.ToDate.HasValue && request.FromDate > request.ToDate)
            return Errors.IncomingPayment.InvalidDateRange;

        try
        {
            var payments = request.FromDate.HasValue && request.ToDate.HasValue
                ? await sapClient.GetIncomingPaymentsByCustomerAsync(
                    request.CardCode,
                    request.FromDate.Value,
                    request.ToDate.Value,
                    cancellationToken)
                : await sapClient.GetIncomingPaymentsByCustomerAsync(request.CardCode, cancellationToken);

            logger.LogInformation("Retrieved {Count} incoming payments for customer {CardCode}", payments.Count, request.CardCode);

            return new IncomingPaymentDateResponseDto
            {
                FromDate = request.FromDate?.ToString("yyyy-MM-dd"),
                ToDate = request.ToDate?.ToString("yyyy-MM-dd"),
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
