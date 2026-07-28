using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// An A/R incoming payment as the SAP Service Layer returns it (<c>SAPB1.Payment</c>).
/// </summary>
/// <remarks>
/// This type once declared <c>CheckSum</c>, <c>CreditSum</c>, <c>DocTotal</c>, <c>DocTotalFc</c>,
/// <c>TransferSumFC</c> and <c>DocDueDate</c> as bound fields. None of them exist on
/// <c>SAPB1.Payment</c> (checked against reference/sap-service-layer-metadata.xml), so they
/// deserialized to zero on every payment ever read, and callers that summed them silently dropped
/// cheque and card money. They are now computed from the rows SAP does return, and are get-only so
/// nothing can mistake them for wire fields again.
/// </remarks>
public class IncomingPayment
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    /// <summary>SAP calls this <c>DueDate</c> on a payment, not <c>DocDueDate</c>.</summary>
    [JsonPropertyName("DueDate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("CardCode")]
    public string? CardCode { get; set; }

    [JsonPropertyName("CardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("DocCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("CashSum")]
    public decimal CashSum { get; set; }

    [JsonPropertyName("CashSumFC")]
    public decimal CashSumFC { get; set; }

    [JsonPropertyName("TransferSum")]
    public decimal TransferSum { get; set; }

    /// <summary>
    /// Total paid by cheque, summed from <see cref="PaymentChecks"/>. SAP keeps no header field for
    /// this; the cheques are the only record of it.
    /// </summary>
    [JsonIgnore]
    public decimal CheckSum => PaymentChecks?.Sum(check => check.CheckSum) ?? 0m;

    /// <summary>
    /// Total paid by credit card, summed from <see cref="PaymentCreditCards"/>. As with cheques,
    /// SAP keeps no header field for it.
    /// </summary>
    [JsonIgnore]
    public decimal CreditSum => PaymentCreditCards?.Sum(card => card.CreditSum) ?? 0m;

    /// <summary>
    /// What the payment is worth, composed from its four means of payment.
    /// </summary>
    /// <remarks>
    /// <c>SAPB1.Payment</c> has no document total at all — unlike a marketing document, a payment is
    /// only ever the sum of how it was paid. Amounts are taken in the payment's own currency, which
    /// is how the rest of the reporting treats a payment; a cheque row drawn in a different currency
    /// from the payment header would need converting, and is not handled here.
    /// </remarks>
    [JsonIgnore]
    public decimal DocTotal => CashSum + TransferSum + CheckSum + CreditSum;

    [JsonPropertyName("Remarks")]
    public string? Remarks { get; set; }

    [JsonPropertyName("JournalRemarks")]
    public string? JournalRemarks { get; set; }

    [JsonPropertyName("TransferReference")]
    public string? TransferReference { get; set; }

    [JsonPropertyName("TransferDate")]
    public string? TransferDate { get; set; }

    [JsonPropertyName("TransferAccount")]
    public string? TransferAccount { get; set; }

    [JsonPropertyName("Cancelled")]
    public string? Cancelled { get; set; }

    [JsonPropertyName("PaymentInvoices")]
    public List<PaymentInvoice>? PaymentInvoices { get; set; }

    [JsonPropertyName("PaymentChecks")]
    public List<PaymentCheck>? PaymentChecks { get; set; }

    [JsonPropertyName("PaymentCreditCards")]
    public List<PaymentCreditCard>? PaymentCreditCards { get; set; }
}

public class PaymentInvoice
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("SumApplied")]
    public decimal SumApplied { get; set; }

    /// <summary>SAP calls this <c>AppliedFC</c>; it was bound as <c>SumAppliedFC</c> and so was
    /// always zero.</summary>
    [JsonPropertyName("AppliedFC")]
    public decimal SumAppliedFC { get; set; }

    [JsonPropertyName("InvoiceType")]
    public string? InvoiceType { get; set; }
}

public class PaymentCheck
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("DueDate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("CheckNumber")]
    public int CheckNumber { get; set; }

    [JsonPropertyName("BankCode")]
    public string? BankCode { get; set; }

    [JsonPropertyName("Branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("AccounttNum")]
    public string? AccountNum { get; set; }

    [JsonPropertyName("CheckSum")]
    public decimal CheckSum { get; set; }

    [JsonPropertyName("Currency")]
    public string? Currency { get; set; }
}

public class PaymentCreditCard
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("CreditCard")]
    public int CreditCard { get; set; }

    [JsonPropertyName("CreditCardNumber")]
    public string? CreditCardNumber { get; set; }

    [JsonPropertyName("CardValidUntil")]
    public string? CardValidUntil { get; set; }

    [JsonPropertyName("VoucherNum")]
    public string? VoucherNum { get; set; }

    [JsonPropertyName("CreditSum")]
    public decimal CreditSum { get; set; }

    [JsonPropertyName("CreditCur")]
    public string? CreditCur { get; set; }
}
