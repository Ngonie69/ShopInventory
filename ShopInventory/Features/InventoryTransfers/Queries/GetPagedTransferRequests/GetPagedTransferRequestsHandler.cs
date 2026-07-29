using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;

public sealed class GetPagedTransferRequestsHandler(
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    IOptions<SAPSettings> settings,
    ILogger<GetPagedTransferRequestsHandler> logger
) : IRequestHandler<GetPagedTransferRequestsQuery, ErrorOr<TransferRequestListResponseDto>>
{
    public async Task<ErrorOr<TransferRequestListResponseDto>> Handle(
        GetPagedTransferRequestsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        if (!TryMapDocumentStatus(request.Status, out var documentStatus))
            return Errors.InventoryTransfer.ValidationFailed(
                $"Unknown status '{request.Status}'. Use open, closed or all.");

        try
        {
            var page = request.Page < 1 ? 1 : request.Page;
            var pageSize = request.PageSize < 1 ? 20 : request.PageSize > 100 ? 100 : request.PageSize;

            var transferRequests = await sapClient.GetPagedInventoryTransferRequestsAsync(
                page, pageSize, documentStatus, cancellationToken);

            logger.LogInformation("Retrieved {Count} transfer requests (page {Page}, status {Status})",
                transferRequests.Count, page, documentStatus ?? "all");

            var transferRequestDtos = transferRequests.ToDto();
            await approvalService.EnrichAsync(transferRequestDtos, cancellationToken);

            return new TransferRequestListResponseDto
            {
                Status = documentStatus,
                Page = page,
                PageSize = pageSize,
                Count = transferRequests.Count,
                HasMore = transferRequests.Count == pageSize,
                TransferRequests = transferRequestDtos
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving paged transfer requests");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving transfer requests");
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }

    /// <summary>
    /// Maps the caller's status to the SAP literal, or null for "every status". Only known values
    /// reach the OData filter, so no caller-supplied text is ever interpolated into it.
    /// </summary>
    private static bool TryMapDocumentStatus(string? status, out string? documentStatus)
    {
        documentStatus = null;

        if (string.IsNullOrWhiteSpace(status))
            return true;

        switch (status.Trim().ToLowerInvariant())
        {
            case "all":
                return true;
            case "open":
            case "bost_open":
                documentStatus = "bost_Open";
                return true;
            case "closed":
            case "close":
            case "bost_close":
                documentStatus = "bost_Close";
                return true;
            default:
                return false;
        }
    }
}
