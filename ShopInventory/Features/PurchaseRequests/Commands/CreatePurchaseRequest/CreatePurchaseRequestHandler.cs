using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseRequests;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseRequests.Commands.CreatePurchaseRequest;

public sealed class CreatePurchaseRequestHandler(
    ISAPServiceLayerClient sapClient,
    IIdempotencyRequestStore idempotencyRequestStore,
    INotificationService notificationService,
    ILogger<CreatePurchaseRequestHandler> logger
) : IRequestHandler<CreatePurchaseRequestCommand, ErrorOr<PurchaseRequestDto>>
{
    public Task<ErrorOr<PurchaseRequestDto>> Handle(
        CreatePurchaseRequestCommand command,
        CancellationToken cancellationToken)
        => IdempotentCreate.RunAsync(
            idempotencyRequestStore,
            logger,
            "purchaserequests.create",
            "purchase request creation",
            command.Request.ClientRequestId,
            command.Request,
            token => CreateAsync(command, token),
            cancellationToken);

    private async Task<ErrorOr<PurchaseRequestDto>> CreateAsync(
        CreatePurchaseRequestCommand command,
        CancellationToken cancellationToken)
    {
        // The last safe abort. From here the post and everything after it run on
        // CancellationToken.None: a caller who closes the tab mid-post must not abort a
        // document SAP may already be committing.
        if (cancellationToken.IsCancellationRequested)
        {
            return Errors.PurchaseRequest.CreationFailed("The request was cancelled before anything was sent to SAP");
        }

        try
        {
            var purchaseRequest = await sapClient.CreatePurchaseRequestAsync(command.Request, CancellationToken.None);
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
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase request notification for DocEntry {DocEntry}", purchaseRequestDto.DocEntry);
            }

            return purchaseRequestDto;
        }
        catch (SapRequestRejectedException rejected)
        {
            // SAP answered, and the answer was no: nothing exists, so the claim is given back and
            // the caller can correct the document and send it again under the same key.
            logger.LogWarning(rejected, "SAP refused the purchase request");
            return Errors.PurchaseRequest.CreationFailed(rejected.SapMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase request");

            // Only a failure that proves nothing was sent may be reported as a plain failure. A
            // timeout or a reply that never arrived leaves the purchase request possibly in SAP, and
            // this document carries nothing SAP could be asked about afterwards.
            return SapFailureClassifier.DefinitelyNotCommitted(ex)
                ? Errors.PurchaseRequest.CreationFailed(ex.Message)
                : Errors.Idempotency.OutcomeUnknown("purchase request");
        }
    }
}