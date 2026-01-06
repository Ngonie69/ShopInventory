using System.Text.Json.Serialization;

namespace ShopInventory.Models;

public class IncomingPayment
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("DocDueDate")]
    public string? DocDueDate { get; set; }

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

    [JsonPropertyName("CheckSum")]
    public decimal CheckSum { get; set; }

    [JsonPropertyName("TransferSum")]
    public decimal TransferSum { get; set; }

    [JsonPropertyName("TransferSumFC")]
    public decimal TransferSumFC { get; set; }

    [JsonPropertyName("CreditSum")]
    public decimal CreditSum { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("DocTotalFc")]
    public decimal DocTotalFc { get; set; }

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

    [JsonPropertyName("SumAppliedFC")]
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
