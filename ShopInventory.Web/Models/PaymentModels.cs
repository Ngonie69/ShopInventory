namespace ShopInventory.Web.Models;

public class IncomingPaymentDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? DocCurrency { get; set; }
    public decimal CashSum { get; set; }
    public decimal CheckSum { get; set; }
    public decimal TransferSum { get; set; }
    public decimal CreditSum { get; set; }
    public decimal DocTotal { get; set; }
    public string? Remarks { get; set; }
    public string? TransferReference { get; set; }
    public string? TransferDate { get; set; }
    public string? TransferAccount { get; set; }
    public List<PaymentInvoiceDto>? PaymentInvoices { get; set; }
    public List<PaymentCheckDto>? PaymentChecks { get; set; }
    public List<PaymentCreditCardDto>? PaymentCreditCards { get; set; }
}

public class PaymentInvoiceDto
{
    public int LineNum { get; set; }
    public int DocEntry { get; set; }
    public decimal SumApplied { get; set; }
    public string? InvoiceType { get; set; }
}

public class PaymentCheckDto
{
    public int LineNum { get; set; }
    public string? DueDate { get; set; }
    public int CheckNumber { get; set; }
    public string? BankCode { get; set; }
    public string? Branch { get; set; }
    public string? AccountNum { get; set; }
    public decimal CheckSum { get; set; }
    public string? Currency { get; set; }
}

public class PaymentCreditCardDto
{
    public int LineNum { get; set; }
    public int CreditCard { get; set; }
    public string? CreditCardNumber { get; set; }
    public string? CardValidUntil { get; set; }
    public string? VoucherNum { get; set; }
    public decimal CreditSum { get; set; }
    public string? CreditCur { get; set; }
}

public class IncomingPaymentListResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<IncomingPaymentDto>? Payments { get; set; }
}

public class IncomingPaymentDateResponse
{
    public string? Date { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public int Count { get; set; }
    public List<IncomingPaymentDto>? Payments { get; set; }
}
