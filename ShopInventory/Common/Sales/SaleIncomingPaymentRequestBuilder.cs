using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Turns a till sale's tender into the SAP incoming payment that settles its invoice.
/// </summary>
/// <remarks>
/// SAP expresses payment means as parallel sums on one document rather than as a type field, so the
/// tender is carried by choosing which sum to fill. GL accounts are deliberately left unset: the
/// system's default account for each means applies, which is what was agreed rather than an oversight.
/// One consequence to know about — Ecocash and Innbucks are both transfers, so under those defaults
/// they settle to the same account. They stay tellable apart by the transfer reference and by the
/// sale's own payment method.
/// </remarks>
public static class SaleIncomingPaymentRequestBuilder
{
    /// <summary>
    /// Builds the payment, or explains why the sale cannot be settled automatically.
    /// </summary>
    /// <param name="sale">The sale, whose <c>PaymentMethod</c> decides the payment means.</param>
    /// <param name="invoiceDocEntry">The SAP invoice this payment is applied to.</param>
    /// <param name="swipeCreditCardCode">
    /// The SAP credit-card code to book a card swipe against. Until one is configured a swipe cannot be
    /// settled: SAP wants a card line with a <c>CreditSum</c>, and inventing a code would book real
    /// money against the wrong one.
    /// </param>
    public static SaleIncomingPaymentBuildResult Build(
        DesktopSaleEntity sale,
        int invoiceDocEntry,
        int? swipeCreditCardCode)
    {
        var paymentSum = TenderTypes.ToPaymentSum(sale.PaymentMethod);

        if (paymentSum is null)
        {
            // No guess. TenderTypes returns null for a tender it does not recognise, and defaulting to
            // cash here is exactly the bug this replaced: money in the wrong SAP account, silently.
            return SaleIncomingPaymentBuildResult.NotMapped(
                string.IsNullOrWhiteSpace(sale.PaymentMethod)
                    ? "The sale records no payment method, so it cannot be settled automatically."
                    : $"Payment method '{sale.PaymentMethod}' does not map to a SAP payment means.");
        }

        if (paymentSum == PaymentSum.Credit && swipeCreditCardCode is null)
        {
            return SaleIncomingPaymentBuildResult.NotMapped(
                "A card swipe needs SAP:SwipeCreditCardCode configured before it can be settled.");
        }

        var request = new CreateIncomingPaymentRequest
        {
            CardCode = sale.CardCode,
            DocDate = sale.DocDate.ToString("yyyy-MM-dd"),
            // Deterministic, so the payment can be found from the till receipt by a human reading SAP.
            Remarks = $"Till sale {sale.ExternalReferenceId}",
            ClientRequestId = sale.ExternalReferenceId,
            PaymentInvoices =
            [
                new PaymentInvoiceRequest
                {
                    DocEntry = invoiceDocEntry,
                    SumApplied = sale.AmountPaid
                }
            ]
        };

        switch (paymentSum)
        {
            case PaymentSum.Cash:
                request.CashSum = sale.AmountPaid;
                break;

            case PaymentSum.Transfer:
                request.TransferSum = sale.AmountPaid;
                request.TransferReference = sale.PaymentReference;
                request.TransferDate = sale.DocDate.ToString("yyyy-MM-dd");
                break;

            case PaymentSum.Credit:
                request.CreditSum = sale.AmountPaid;
                request.PaymentCreditCards =
                [
                    new PaymentCreditCardRequest
                    {
                        CreditCard = swipeCreditCardCode!.Value,
                        CreditSum = sale.AmountPaid,
                        VoucherNum = sale.PaymentReference
                    }
                ];
                break;
        }

        return SaleIncomingPaymentBuildResult.Mapped(request);
    }
}

/// <summary>
/// Either a payment to post, or the reason there isn't one. Kept as a result rather than a null so the
/// caller has something readable to store on the sale and show in the review queue.
/// </summary>
public sealed class SaleIncomingPaymentBuildResult
{
    private SaleIncomingPaymentBuildResult(CreateIncomingPaymentRequest? request, string? reason)
    {
        Request = request;
        Reason = reason;
    }

    public CreateIncomingPaymentRequest? Request { get; }

    public string? Reason { get; }

    public bool CanPost => Request is not null;

    public static SaleIncomingPaymentBuildResult Mapped(CreateIncomingPaymentRequest request) =>
        new(request, null);

    public static SaleIncomingPaymentBuildResult NotMapped(string reason) =>
        new(null, reason);
}
