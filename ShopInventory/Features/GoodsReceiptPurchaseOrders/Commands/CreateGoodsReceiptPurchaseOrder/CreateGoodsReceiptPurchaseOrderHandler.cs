using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.GoodsReceiptPurchaseOrders;
using ShopInventory.Services;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;

public sealed class CreateGoodsReceiptPurchaseOrderHandler(
    ISAPServiceLayerClient sapClient,
    IIdempotencyRequestStore idempotencyRequestStore,
    INotificationService notificationService,
    ILogger<CreateGoodsReceiptPurchaseOrderHandler> logger
) : IRequestHandler<CreateGoodsReceiptPurchaseOrderCommand, ErrorOr<GoodsReceiptPurchaseOrderDto>>
{
    public Task<ErrorOr<GoodsReceiptPurchaseOrderDto>> Handle(
        CreateGoodsReceiptPurchaseOrderCommand command,
        CancellationToken cancellationToken)
        => IdempotentCreate.RunAsync(
            idempotencyRequestStore,
            logger,
            "goodsreceiptpurchaseorders.create",
            "goods receipt PO creation",
            command.Request.ClientRequestId,
            command.Request,
            token => CreateAsync(command, token),
            cancellationToken);

    private async Task<ErrorOr<GoodsReceiptPurchaseOrderDto>> CreateAsync(
        CreateGoodsReceiptPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        // The last safe abort. From here the post and everything after it run on
        // CancellationToken.None: a caller who closes the tab mid-post must not abort a
        // document SAP may already be committing.
        if (cancellationToken.IsCancellationRequested)
        {
            return Errors.GoodsReceiptPurchaseOrder.CreationFailed("The request was cancelled before anything was sent to SAP");
        }

        try
        {
            var goodsReceipt = await sapClient.CreateGoodsReceiptPurchaseOrderAsync(command.Request, CancellationToken.None);
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
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish goods receipt PO notification for DocEntry {DocEntry}", goodsReceiptDto.DocEntry);
            }

            return goodsReceiptDto;
        }
        catch (SapRequestRejectedException rejected)
        {
            // SAP answered, and the answer was no: nothing exists, so the claim is given back and
            // the caller can correct the document and send it again under the same key.
            logger.LogWarning(rejected, "SAP refused the goods receipt PO");
            return Errors.GoodsReceiptPurchaseOrder.CreationFailed(rejected.SapMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating goods receipt PO for supplier {CardCode}", command.Request.CardCode);

            // Only a failure that proves nothing was sent may be reported as a plain failure. A
            // timeout or a reply that never arrived leaves the goods receipt PO possibly in SAP, and
            // this document carries nothing SAP could be asked about afterwards.
            return SapFailureClassifier.DefinitelyNotCommitted(ex)
                ? Errors.GoodsReceiptPurchaseOrder.CreationFailed(ex.Message)
                : Errors.Idempotency.OutcomeUnknown("goods receipt PO");
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