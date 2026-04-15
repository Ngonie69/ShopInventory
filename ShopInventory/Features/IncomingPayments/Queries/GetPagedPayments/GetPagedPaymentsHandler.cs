using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPagedPayments;

public sealed class GetPagedPaymentsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPagedPaymentsHandler> logger
) : IRequestHandler<GetPagedPaymentsQuery, ErrorOr<IncomingPaymentListResponseDto>>
{
    public async Task<ErrorOr<IncomingPaymentListResponseDto>> Handle(
        GetPagedPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        try
        {
            var page = request.Page < 1 ? 1 : request.Page;
            var pageSize = request.PageSize < 1 ? 20 : request.PageSize > 100 ? 100 : request.PageSize;

            var payments = await sapClient.GetPagedIncomingPaymentsAsync(page, pageSize, cancellationToken);

            logger.LogInformation("Retrieved {Count} incoming payments (page {Page})", payments.Count, page);

            return new IncomingPaymentListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                Count = payments.Count,
                HasMore = payments.Count == pageSize,
                Payments = payments.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.IncomingPayment.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving incoming payments");
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving incoming payments");
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }
}
