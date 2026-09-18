using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseQuotations;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;

public sealed class CreatePurchaseQuotationHandler(
    ISAPServiceLayerClient sapClient,
    IIdempotencyRequestStore idempotencyRequestStore,
    INotificationService notificationService,
    ILogger<CreatePurchaseQuotationHandler> logger
) : IRequestHandler<CreatePurchaseQuotationCommand, ErrorOr<PurchaseQuotationDto>>
{
    public Task<ErrorOr<PurchaseQuotationDto>> Handle(
        CreatePurchaseQuotationCommand command,
        CancellationToken cancellationToken)
        => IdempotentCreate.RunAsync(
            idempotencyRequestStore,
            logger,
            "purchasequotations.create",
            "purchase quotation creation",
            command.Request.ClientRequestId,
            command.Request,
            token => CreateAsync(command, token),
            cancellationToken);

    private async Task<ErrorOr<PurchaseQuotationDto>> CreateAsync(
        CreatePurchaseQuotationCommand command,
        CancellationToken cancellationToken)
    {
        // The last safe abort. From here the post and everything after it run on
        // CancellationToken.None: a caller who closes the tab mid-post must not abort a
        // document SAP may already be committing.
        if (cancellationToken.IsCancellationRequested)
        {
            return Errors.PurchaseQuotation.CreationFailed("The request was cancelled before anything was sent to SAP");
        }

        try
        {
            var purchaseQuotation = await sapClient.CreatePurchaseQuotationAsync(command.Request, CancellationToken.None);
            var purchaseQuotationDto = PurchaseQuotationMappings.MapFromSap(purchaseQuotation);

            try
            {
                var supplierDisplay = BuildBusinessPartnerDisplay(purchaseQuotationDto.CardCode, purchaseQuotationDto.CardName);
                var totalDisplay = BuildMoneyDisplay(purchaseQuotationDto.DocCurrency, purchaseQuotationDto.DocTotal);

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Purchase Quotation Created: #{purchaseQuotationDto.DocNum}",
                        $"Purchase quotation #{purchaseQuotationDto.DocNum} for {supplierDisplay} totaling {totalDisplay} was created successfully.",
                        "Success",
                        "PurchaseQuotation",
                        "PurchaseQuotation",
                        purchaseQuotationDto.DocEntry.ToString(),
                        "/purchase-quotations",
                        new Dictionary<string, string>
                        {
                            ["docEntry"] = purchaseQuotationDto.DocEntry.ToString(),
                            ["docNum"] = purchaseQuotationDto.DocNum.ToString(),
                            ["cardCode"] = purchaseQuotationDto.CardCode ?? string.Empty,
                            ["cardName"] = purchaseQuotationDto.CardName ?? string.Empty,
                            ["docCurrency"] = purchaseQuotationDto.DocCurrency ?? string.Empty,
                            ["docTotal"] = purchaseQuotationDto.DocTotal.ToString("N2")
                        }),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase quotation notification for DocEntry {DocEntry}", purchaseQuotationDto.DocEntry);
            }

            return purchaseQuotationDto;
        }
        catch (SapRequestRejectedException rejected)
        {
            // SAP answered, and the answer was no: nothing exists, so the claim is given back and
            // the caller can correct the document and send it again under the same key.
            logger.LogWarning(rejected, "SAP refused the purchase quotation");
            return Errors.PurchaseQuotation.CreationFailed(rejected.SapMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase quotation for supplier {CardCode}", command.Request.CardCode);

            // Only a failure that proves nothing was sent may be reported as a plain failure. A
            // timeout or a reply that never arrived leaves the purchase quotation possibly in SAP, and
            // this document carries nothing SAP could be asked about afterwards.
            return SapFailureClassifier.DefinitelyNotCommitted(ex)
                ? Errors.PurchaseQuotation.CreationFailed(ex.Message)
                : Errors.Idempotency.OutcomeUnknown("purchase quotation");
        }
    }

    private static string BuildBusinessPartnerDisplay(string? cardCode, string? cardName)
    {
        var normalizedCode = cardCode?.Trim();
        var normalizedName = cardName?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode ?? "unknown supplier";
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        return $"{normalizedCode} - {normalizedName}";
    }

    private static string BuildMoneyDisplay(string? currency, decimal total)
        => string.IsNullOrWhiteSpace(currency)
            ? total.ToString("N2")
            : $"{currency} {total:N2}";
}