namespace ShopInventory.Web.Models;

/// <summary>
/// Mirrors <c>ShopInventory.DTOs.GLAccountLedgerResponseDto</c>.
/// </summary>
/// <remarks>
/// Property for property, and nullable for nullable. The two are separate types by hand rather than
/// a shared assembly, so a mismatch is not a compile error — it is a
/// <see cref="System.Text.Json.JsonException"/> at read time, which the page surfaces as "no data"
/// and which took two months to find the last time it happened.
/// </remarks>
public class GLAccountLedgerResponse
{
    public string AccountCode { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? AccountType { get; set; }
    public string? Currency { get; set; }

    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime GeneratedAt { get; set; }

    public decimal OpeningBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal ClosingBalance { get; set; }

    /// <summary>What SAP's chart of accounts reports for the account right now.</summary>
    public decimal SapBalance { get; set; }

    /// <summary>The journal summed to today — comparable with <see cref="SapBalance"/>.</summary>
    public decimal ComputedBalanceToday { get; set; }

    /// <summary>Zero is the expected answer. Anything else means the two disagree.</summary>
    public decimal ReconciliationDifference { get; set; }

    /// <summary>False when the check could not be run at all, which is not the same as agreeing.</summary>
    public bool IsReconciled { get; set; }

    /// <summary>
    /// The tail of the period was dropped. There is no total to go with it on purpose — counting
    /// the rest means reading the rest, which is the cost the cap avoids.
    /// </summary>
    public bool IsTruncated { get; set; }

    public int LineLimit { get; set; }

    public List<GLAccountLedgerLine> Lines { get; set; } = new();
}

public class GLAccountLedgerLine
{
    public DateTime Date { get; set; }
    public int TransactionNumber { get; set; }
    public string OriginCode { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string? OriginNumber { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string? PartnerCode { get; set; }
    public string? OffsetAccount { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public string? Currency { get; set; }
    public string? CreatedBy { get; set; }
}
