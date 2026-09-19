using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Sales;

/// <summary>
/// What was paid against one invoice, by the SAP payment sum it belongs in.
/// </summary>
public readonly record struct TenderSplit(decimal Cash, decimal Transfer, decimal Credit)
{
    public decimal Total => Cash + Transfer + Credit;

    public TenderSplit Add(TenderSplit other) =>
        new(Cash + other.Cash, Transfer + other.Transfer, Credit + other.Credit);
}

/// <summary>
/// Either a tender split, or the reason there isn't one.
/// </summary>
public sealed class TenderSplitResult
{
    private TenderSplitResult(TenderSplit? split, string? reason)
    {
        Split = split;
        Reason = reason;
    }

    public TenderSplit? Split { get; }

    public string? Reason { get; }

    public bool IsMapped => Split is not null;

    public static TenderSplitResult Mapped(TenderSplit split) => new(split, null);

    public static TenderSplitResult NotMapped(string reason) => new(null, reason);
}

/// <summary>
/// The G/L accounts a payment is sent with: one for cash, one for everything else.
/// </summary>
public readonly record struct PaymentAccounts(string CashAccount, string ElectronicAccount)
{
    public static PaymentAccounts From(IncomingPaymentGlMappingEntity mapping) =>
        new(mapping.CashAccount.Trim(), mapping.ElectronicAccount.Trim());
}

/// <summary>
/// Turns a day's till, vending, van and consolidated invoices into the one SAP incoming payment that
/// settles them for a business partner.
/// </summary>
/// <remarks>
/// <para>
/// SAP stores payment means as separate sums on one document, not as a type field. So a payment
/// covering many sales carries each tender's total in its own sum: cash in CashSum, the wallets in
/// TransferSum. Before this, the older consolidation put the whole day under whichever tender was most
/// common, which booked wallet and card money as cash.
/// </para>
/// <para>
/// Every account is named, from the partner's <see cref="IncomingPaymentGlMappingEntity"/>. Left unset,
/// SAP applies its default, 700300, which is the factory's cash on hand.
/// </para>
/// </remarks>
public static class DailyIncomingPaymentBuilder
{
    /// <summary>The longest Remarks SAP keeps on a payment.</summary>
    private const int MaxRemarksLength = 254;

    /// <summary>The longest JournalRemarks SAP keeps.</summary>
    private const int MaxJournalRemarksLength = 50;

    /// <summary>
    /// The payment's business key. It goes at the start of the SAP Remarks, and a lost reply is
    /// resolved by looking for it there. It is also the payment's SAP Reference, for audit.
    /// </summary>
    public static string ReferenceFor(DateTime paymentDate, string cardCode) =>
        $"DAYPAY-{paymentDate:yyyyMMdd}-{cardCode.Trim()}";

    /// <summary>
    /// What a till or vending sale paid, by tender.
    /// </summary>
    /// <param name="sale">The sale, whose <c>PaymentMethod</c> decides the payment sum.</param>
    public static TenderSplitResult SplitSale(DesktopSaleEntity sale) =>
        SplitAmount(sale.PaymentMethod, sale.AmountPaid);

    /// <summary>
    /// Which tender an online van sale was paid in. The amount is decided later, from the SAP invoice.
    /// </summary>
    /// <remarks>
    /// The amount here only marks the tender. The line pays the invoice's open balance instead (see
    /// <see cref="DailyIncomingPaymentLineEntity.PayOpenBalance"/>), because the reservation's value is
    /// net of VAT. A handset built before the payment picker records no tender. That counts as cash,
    /// which cannot misplace money, because a van's two accounts are the same account.
    /// </remarks>
    public static TenderSplitResult SplitReservation(StockReservationEntity reservation)
    {
        var amount = Math.Max(reservation.TotalValue, 0.01m);
        return string.IsNullOrWhiteSpace(reservation.PaymentMethod)
            ? TenderSplitResult.Mapped(new TenderSplit(amount, 0m, 0m))
            : SplitAmount(reservation.PaymentMethod, amount);
    }

    /// <summary>
    /// What an older desktop app consolidated invoice was paid, by tender.
    /// </summary>
    /// <param name="consolidation">The consolidation whose invoice is being settled.</param>
    /// <param name="linkedSales">The desktop sales recorded against it.</param>
    /// <remarks>
    /// A consolidation also folds in fiscalised queue entries. Those are never saved as sales, so they
    /// have no rows here. The consolidation always treated each one as paid in full in cash, so what the
    /// linked sales do not account for is taken as cash. Any sale whose tender cannot be mapped makes the
    /// whole invoice unmappable: one payment cannot be split, and guessing cash is exactly the error this
    /// replaces.
    /// </remarks>
    public static TenderSplitResult SplitConsolidation(
        SaleConsolidationEntity consolidation,
        IReadOnlyCollection<DesktopSaleEntity> linkedSales)
    {
        var split = new TenderSplit(0m, 0m, 0m);

        foreach (var sale in linkedSales.Where(sale => sale.AmountPaid > 0))
        {
            var part = SplitSale(sale);
            if (!part.IsMapped)
            {
                return TenderSplitResult.NotMapped($"Sale {sale.ExternalReferenceId}: {part.Reason}");
            }

            split = split.Add(part.Split!.Value);
        }

        var queued = consolidation.TotalAmount - linkedSales.Sum(sale => sale.TotalAmount);
        if (queued > 0)
        {
            split = split.Add(new TenderSplit(queued, 0m, 0m));
        }

        return TenderSplitResult.Mapped(split);
    }

    /// <summary>
    /// Reduces a single-tender split so it applies no more than the invoice still owes.
    /// </summary>
    /// <remarks>
    /// Only used for a till or vending sale, which has one tender, so there is no choice about which
    /// tender to reduce. The usual reason an invoice owes less than was paid is a credit memo raised
    /// against it for a return, and the refund left the till as the same tender.
    /// </remarks>
    public static TenderSplit CapTo(TenderSplit split, decimal openBalance)
    {
        if (openBalance <= 0m)
        {
            return new TenderSplit(0m, 0m, 0m);
        }

        var excess = split.Total - openBalance;
        if (excess <= 0m)
        {
            return split;
        }

        var credit = Math.Max(0m, split.Credit - excess);
        excess -= split.Credit - credit;
        var transfer = Math.Max(0m, split.Transfer - excess);
        excess -= split.Transfer - transfer;
        var cash = Math.Max(0m, split.Cash - excess);

        return new TenderSplit(cash, transfer, credit);
    }

    /// <summary>
    /// Builds the SAP incoming payment for a business partner's day from its claimed lines.
    /// </summary>
    /// <remarks>
    /// Card money stays recorded on the lines as card money, so reports can still tell a swipe from a
    /// wallet. It reaches SAP as transfer money: the acquirer pays the bank, and this company database
    /// has no credit card records. So wallets and swipes share TransferSum and the partner's electronic
    /// account, and the payment carries no card line.
    /// </remarks>
    public static CreateIncomingPaymentRequest BuildRequest(
        DailyIncomingPaymentEntity payment,
        PaymentAccounts accounts)
    {
        if (string.IsNullOrWhiteSpace(accounts.CashAccount) || string.IsNullOrWhiteSpace(accounts.ElectronicAccount))
        {
            throw new InvalidOperationException(
                $"Daily payment {payment.Reference} has no G/L account to post to, and SAP would use its default.");
        }

        var lines = payment.Lines.Where(line => line.SumApplied > 0).ToList();
        var cash = lines.Sum(line => line.CashAmount);
        var transferSum = lines.Sum(line => line.TransferAmount + line.CreditAmount);
        var date = payment.PaymentDate.ToString("yyyy-MM-dd");

        var request = new CreateIncomingPaymentRequest
        {
            CardCode = payment.CardCode,
            DocDate = date,
            Remarks = Truncate(
                $"{payment.Reference}: daily payment for {lines.Count} invoice(s)", MaxRemarksLength),
            CounterReference = payment.Reference,
            JournalRemarks = Truncate(payment.Reference, MaxJournalRemarksLength),
            ClientRequestId = payment.Reference,
            CashSum = cash,
            TransferSum = transferSum,
            PaymentInvoices = lines
                .Select(line => new PaymentInvoiceRequest
                {
                    DocEntry = line.InvoiceDocEntry,
                    SumApplied = line.SumApplied
                })
                .ToList()
        };

        if (cash > 0m)
        {
            request.CashAccount = accounts.CashAccount;
        }

        if (transferSum > 0m)
        {
            // The individual wallet references stay on the sales. SAP's transfer reference is too short
            // to hold a day's worth of them.
            request.TransferDate = date;
            request.TransferAccount = accounts.ElectronicAccount;
        }

        return request;
    }

    private static TenderSplitResult SplitAmount(string? paymentMethod, decimal amount)
    {
        var paymentSum = TenderTypes.ToPaymentSum(paymentMethod);

        if (paymentSum is null)
        {
            // No guess. Defaulting an unrecognised tender to cash puts money in the wrong SAP account
            // without anyone noticing.
            return TenderSplitResult.NotMapped(
                string.IsNullOrWhiteSpace(paymentMethod)
                    ? "The sale records no payment method, so it cannot be settled automatically."
                    : $"Payment method '{paymentMethod}' does not map to a SAP payment means.");
        }

        return TenderSplitResult.Mapped(paymentSum switch
        {
            PaymentSum.Cash => new TenderSplit(amount, 0m, 0m),
            PaymentSum.Transfer => new TenderSplit(0m, amount, 0m),
            _ => new TenderSplit(0m, 0m, amount)
        });
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
