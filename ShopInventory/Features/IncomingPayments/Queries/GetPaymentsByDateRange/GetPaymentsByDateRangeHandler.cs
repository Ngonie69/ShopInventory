using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByDateRange;

public sealed class GetPaymentsByDateRangeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPaymentsByDateRangeHandler> logger
) : IRequestHandler<GetPaymentsByDateRangeQuery, ErrorOr<IncomingPaymentDateResponseDto>>
{
    public async Task<ErrorOr<IncomingPaymentDateResponseDto>> Handle(
        GetPaymentsByDateRangeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        if (request.FromDate > request.ToDate)
            return Errors.IncomingPayment.InvalidDateRange;

        try
        {
            var payments = await sapClient.GetIncomingPaymentsByDateRangeAsync(request.FromDate, request.ToDate, cancellationToken);

            logger.LogInformation("Retrieved {Count} incoming payments from {FromDate} to {ToDate}",
                payments.Count, request.FromDate.ToString("yyyy-MM-dd"), request.ToDate.ToString("yyyy-MM-dd"));

            return new IncomingPaymentDateResponseDto
            {
                FromDate = request.FromDate.ToString("yyyy-MM-dd"),
                ToDate = request.ToDate.ToString("yyyy-MM-dd"),
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
            logger.LogError(ex, "Timeout retrieving payments by date range");
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving payments by date range");
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }
}
