namespace ShopInventory.DTOs;

public class CustomerStatementResponseDto
{
    public StatementCustomerDto Customer { get; set; } = new();
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public decimal OpeningBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal TotalInvoices { get; set; }
    public decimal TotalPayments { get; set; }
    public decimal TotalCreditNotes { get; set; }
    public decimal ClosingBalance { get; set; }
    public List<StatementLineDto> Lines { get; set; } = new();
    public StatementAgingSummaryDto Aging { get; set; } = new();
}

public class StatementCustomerDto
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public decimal Balance { get; set; }
    public string? Currency { get; set; }
    public string AccountStructure { get; set; } = "Single";
    public string? PaymentTermsName { get; set; }
    public int? PaymentTermsDays { get; set; }
}

public class StatementLineDto
{
    public DateTime Date { get; set; }
    public int TransactionNumber { get; set; }
    public string OriginCode { get; set; } = string.Empty;
    public string? OriginNumber { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? OffsetAccount { get; set; }
    public string? Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal BalanceDue { get; set; }
    public decimal Balance { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }
    public string? CreatedBy { get; set; }
    public int? DaysOverdue { get; set; }
}

public class StatementAgingSummaryDto
{
    public decimal Current { get; set; }
    public decimal Days1To30 { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Over90Days { get; set; }
    public decimal Total { get; set; }
    public string Bucket1Label { get; set; } = "1-30 Days";
    public string Bucket2Label { get; set; } = "31-60 Days";
    public string Bucket3Label { get; set; } = "61-90 Days";
    public string Bucket4Label { get; set; } = "Over 90 Days";
}
