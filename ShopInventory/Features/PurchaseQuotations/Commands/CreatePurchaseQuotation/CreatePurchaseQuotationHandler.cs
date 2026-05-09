using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseQuotations;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;

public sealed class CreatePurchaseQuotationHandler(
    ISAPServiceLayerClient sapClient,
    INotificationService notificationService,
    ILogger<CreatePurchaseQuotationHandler> logger
) : IRequestHandler<CreatePurchaseQuotationCommand, ErrorOr<PurchaseQuotationDto>>
{
    public async Task<ErrorOr<PurchaseQuotationDto>> Handle(
        CreatePurchaseQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var purchaseQuotation = await sapClient.CreatePurchaseQuotationAsync(command.Request, cancellationToken);
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
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase quotation notification for DocEntry {DocEntry}", purchaseQuotationDto.DocEntry);
            }

            return purchaseQuotationDto;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase quotation for supplier {CardCode}", command.Request.CardCode);
            return Errors.PurchaseQuotation.CreationFailed(ex.Message);
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