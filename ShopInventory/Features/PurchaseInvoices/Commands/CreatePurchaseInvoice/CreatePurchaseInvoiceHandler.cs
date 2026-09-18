using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Features.PurchaseInvoices;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;

public sealed class CreatePurchaseInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    IIdempotencyRequestStore idempotencyRequestStore,
    INotificationService notificationService,
    ILogger<CreatePurchaseInvoiceHandler> logger
) : IRequestHandler<CreatePurchaseInvoiceCommand, ErrorOr<PurchaseInvoiceDto>>
{
    public Task<ErrorOr<PurchaseInvoiceDto>> Handle(
        CreatePurchaseInvoiceCommand command,
        CancellationToken cancellationToken)
        => IdempotentCreate.RunAsync(
            idempotencyRequestStore,
            logger,
            "purchaseinvoices.create",
            "purchase invoice creation",
            command.Request.ClientRequestId,
            command.Request,
            token => CreateAsync(command, token),
            cancellationToken);

    private async Task<ErrorOr<PurchaseInvoiceDto>> CreateAsync(
        CreatePurchaseInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        // The last safe abort. From here the post and everything after it run on
        // CancellationToken.None: a caller who closes the tab mid-post must not abort a
        // document SAP may already be committing.
        if (cancellationToken.IsCancellationRequested)
        {
            return Errors.PurchaseInvoice.CreationFailed("The request was cancelled before anything was sent to SAP");
        }

        try
        {
            var invoice = await sapClient.CreatePurchaseInvoiceAsync(command.Request, CancellationToken.None);
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
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase invoice notification for DocEntry {DocEntry}", invoiceDto.DocEntry);
            }

            return invoiceDto;
        }
        catch (SapRequestRejectedException rejected)
        {
            // SAP answered, and the answer was no: nothing exists, so the claim is given back and
            // the caller can correct the document and send it again under the same key.
            logger.LogWarning(rejected, "SAP refused the purchase invoice");
            return Errors.PurchaseInvoice.CreationFailed(rejected.SapMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase invoice for supplier {CardCode}", command.Request.CardCode);

            // Only a failure that proves nothing was sent may be reported as a plain failure. A
            // timeout or a reply that never arrived leaves the purchase invoice possibly in SAP, and
            // this document carries nothing SAP could be asked about afterwards.
            return SapFailureClassifier.DefinitelyNotCommitted(ex)
                ? Errors.PurchaseInvoice.CreationFailed(ex.Message)
                : Errors.Idempotency.OutcomeUnknown("purchase invoice");
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