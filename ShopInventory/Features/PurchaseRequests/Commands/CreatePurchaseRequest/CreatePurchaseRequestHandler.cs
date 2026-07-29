using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseRequests;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseRequests.Commands.CreatePurchaseRequest;

public sealed class CreatePurchaseRequestHandler(
    ISAPServiceLayerClient sapClient,
    INotificationService notificationService,
    ILogger<CreatePurchaseRequestHandler> logger
) : IRequestHandler<CreatePurchaseRequestCommand, ErrorOr<PurchaseRequestDto>>
{
    public async Task<ErrorOr<PurchaseRequestDto>> Handle(
        CreatePurchaseRequestCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var purchaseRequest = await sapClient.CreatePurchaseRequestAsync(command.Request, cancellationToken);
            var purchaseRequestDto = PurchaseRequestMappings.MapFromSap(purchaseRequest);

            try
            {
                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Purchase Request Created: #{purchaseRequestDto.DocNum}",
                        $"Purchase request #{purchaseRequestDto.DocNum} with {purchaseRequestDto.Lines?.Count ?? 0} line(s) totaling {purchaseRequestDto.DocTotal:N2} was created successfully.",
                        "Success",
                        "PurchaseRequest",
                        "PurchaseRequest",
                        purchaseRequestDto.DocEntry.ToString(),
                        "/purchase-requests",
                        new Dictionary<string, string>
                        {
                            ["docEntry"] = purchaseRequestDto.DocEntry.ToString(),
                            ["docNum"] = purchaseRequestDto.DocNum.ToString(),
                            ["docTotal"] = purchaseRequestDto.DocTotal.ToString("N2"),
                            ["lineCount"] = (purchaseRequestDto.Lines?.Count ?? 0).ToString(),
                            ["requesterName"] = purchaseRequestDto.RequesterName ?? string.Empty
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase request notification for DocEntry {DocEntry}", purchaseRequestDto.DocEntry);
            }

            return purchaseRequestDto;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase request");
            return Errors.PurchaseRequest.CreationFailed(ex.Message);
        }
    }
}