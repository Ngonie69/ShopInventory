using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetTodaysPayments;

public sealed class GetTodaysPaymentsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTodaysPaymentsHandler> logger
) : IRequestHandler<GetTodaysPaymentsQuery, ErrorOr<IncomingPaymentDateResponseDto>>
{
    public async Task<ErrorOr<IncomingPaymentDateResponseDto>> Handle(
        GetTodaysPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        try
        {
            var today = DateTime.Today;
            var payments = await sapClient.GetIncomingPaymentsByDateRangeAsync(today, today, cancellationToken);

            logger.LogInformation("Retrieved {Count} incoming payments for today ({Date})", payments.Count, today.ToString("yyyy-MM-dd"));

            return new IncomingPaymentDateResponseDto
            {
                Date = today.ToString("yyyy-MM-dd"),
                Count = payments.Count,
                Payments = payments.ToDto()
            };
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving today's payments");
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving today's incoming payments");
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }
}
