using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseInvoices;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;

public sealed class CreatePurchaseInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    INotificationService notificationService,
    ILogger<CreatePurchaseInvoiceHandler> logger
) : IRequestHandler<CreatePurchaseInvoiceCommand, ErrorOr<PurchaseInvoiceDto>>
{
    public async Task<ErrorOr<PurchaseInvoiceDto>> Handle(
        CreatePurchaseInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await sapClient.CreatePurchaseInvoiceAsync(command.Request, cancellationToken);
            var invoiceDto = PurchaseInvoiceMappings.MapFromSap(invoice);

            try
            {
                var supplierDisplay = BuildBusinessPartnerDisplay(invoiceDto.CardCode, invoiceDto.CardName);
                var totalDisplay = BuildMoneyDisplay(invoiceDto.DocCurrency, invoiceDto.DocTotal);

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Purchase Invoice Created: #{invoiceDto.DocNum}",
                        $"Purchase invoice #{invoiceDto.DocNum} for {supplierDisplay} totaling {totalDisplay} was created successfully.",
                        "Success",
                        "PurchaseInvoice",
                        "PurchaseInvoice",
                        invoiceDto.DocEntry.ToString(),
                        "/purchase-invoices",
                        new Dictionary<string, string>
                        {
                            ["docEntry"] = invoiceDto.DocEntry.ToString(),
                            ["docNum"] = invoiceDto.DocNum.ToString(),
                            ["cardCode"] = invoiceDto.CardCode ?? string.Empty,
                            ["cardName"] = invoiceDto.CardName ?? string.Empty,
                            ["docCurrency"] = invoiceDto.DocCurrency ?? string.Empty,
                            ["docTotal"] = invoiceDto.DocTotal.ToString("N2")
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase invoice notification for DocEntry {DocEntry}", invoiceDto.DocEntry);
            }

            return invoiceDto;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase invoice for supplier {CardCode}", command.Request.CardCode);
            return Errors.PurchaseInvoice.CreationFailed(ex.Message);
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