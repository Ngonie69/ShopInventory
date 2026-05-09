using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.GoodsReceiptPurchaseOrders;
using ShopInventory.Services;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;

public sealed class CreateGoodsReceiptPurchaseOrderHandler(
    ISAPServiceLayerClient sapClient,
    INotificationService notificationService,
    ILogger<CreateGoodsReceiptPurchaseOrderHandler> logger
) : IRequestHandler<CreateGoodsReceiptPurchaseOrderCommand, ErrorOr<GoodsReceiptPurchaseOrderDto>>
{
    public async Task<ErrorOr<GoodsReceiptPurchaseOrderDto>> Handle(
        CreateGoodsReceiptPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var goodsReceipt = await sapClient.CreateGoodsReceiptPurchaseOrderAsync(command.Request, cancellationToken);
            var goodsReceiptDto = GoodsReceiptPurchaseOrderMappings.MapFromSap(goodsReceipt);

            try
            {
                var supplierDisplay = BuildBusinessPartnerDisplay(goodsReceiptDto.CardCode, goodsReceiptDto.CardName);
                var totalDisplay = BuildMoneyDisplay(goodsReceiptDto.DocCurrency, goodsReceiptDto.DocTotal);

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Goods Receipt PO Created: #{goodsReceiptDto.DocNum}",
                        $"Goods receipt PO #{goodsReceiptDto.DocNum} for {supplierDisplay} totaling {totalDisplay} was created successfully.",
                        "Success",
                        "GoodsReceiptPurchaseOrder",
                        "GoodsReceiptPurchaseOrder",
                        goodsReceiptDto.DocEntry.ToString(),
                        "/goods-receipt-pos",
                        new Dictionary<string, string>
                        {
                            ["docEntry"] = goodsReceiptDto.DocEntry.ToString(),
                            ["docNum"] = goodsReceiptDto.DocNum.ToString(),
                            ["cardCode"] = goodsReceiptDto.CardCode ?? string.Empty,
                            ["cardName"] = goodsReceiptDto.CardName ?? string.Empty,
                            ["docCurrency"] = goodsReceiptDto.DocCurrency ?? string.Empty,
                            ["docTotal"] = goodsReceiptDto.DocTotal.ToString("N2")
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish goods receipt PO notification for DocEntry {DocEntry}", goodsReceiptDto.DocEntry);
            }

            return goodsReceiptDto;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating goods receipt PO for supplier {CardCode}", command.Request.CardCode);
            return Errors.GoodsReceiptPurchaseOrder.CreationFailed(ex.Message);
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