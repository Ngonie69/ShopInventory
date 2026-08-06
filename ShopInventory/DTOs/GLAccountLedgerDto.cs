namespace ShopInventory.DTOs;

/// <summary>
/// The postings against one G/L account over a date range, with the balance they carry.
/// </summary>
public class GLAccountLedgerResponseDto
{
    public string AccountCode { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? AccountType { get; set; }
    public string? Currency { get; set; }

    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime GeneratedAt { get; set; }

    /// <summary>Everything posted before <see cref="FromDate"/>, as debits less credits.</summary>
    public decimal OpeningBalance { get; set; }

    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }

    /// <summary>
    /// <see cref="OpeningBalance"/> plus the period's movement — the balance as at
    /// <see cref="ToDate"/>, not as at today.
    /// </summary>
    public decimal ClosingBalance { get; set; }

    /// <summary>
    /// What SAP's own chart of accounts reports for this account right now.
    /// </summary>
    public decimal SapBalance { get; set; }

    /// <summary>
    /// The same debits-less-credits sum as the opening balance, but run to today — so it is
    /// directly comparable with <see cref="SapBalance"/> whatever date range is being viewed.
    /// </summary>
    public decimal ComputedBalanceToday { get; set; }

    /// <summary>
    /// <see cref="SapBalance"/> less <see cref="ComputedBalanceToday"/>. Zero is the expected
    /// answer; anything else means this page's arithmetic disagrees with SAP's and the numbers on
    /// it should not be trusted until that is explained.
    /// </summary>
    public decimal ReconciliationDifference { get; set; }

    /// <summary>
    /// False when the reconciliation could not be run at all — SAP would not give up the account,
    /// or the balance read failed. The difference is meaningless in that case rather than zero.
    /// </summary>
    public bool IsReconciled { get; set; }

    /// <summary>
    /// True when the period held more lines than <see cref="LineLimit"/> and the tail was dropped.
    /// The opening balance is unaffected — it is a separate sum — but the closing balance shown on
    /// the lines is, so a truncated ledger must say so rather than quietly disagree with itself.
    /// </summary>
    /// <remarks>
    /// There is deliberately no total to go with this. Finding out how many lines the period really
    /// holds means reading them all, which is the cost the cap exists to avoid — so the honest thing
    /// a truncated ledger can say is "more than <see cref="LineLimit"/>", and a count here would be
    /// wherever the paged read happened to stop rather than the answer it looks like.
    /// </remarks>
    public bool IsTruncated { get; set; }

    public int LineLimit { get; set; }

    public List<GLAccountLedgerLineDto> Lines { get; set; } = new();
}

public class GLAccountLedgerLineDto
{
    public DateTime Date { get; set; }
    public int TransactionNumber { get; set; }

    /// <summary>SAP's own abbreviation for the originating document — IN, RC, JE, PS.</summary>
    public string OriginCode { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;
    public string? OriginNumber { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;

    /// <summary>
    /// The business partner the line was posted against, where there is one. On a control account
    /// this is the useful "who" column; on a nominal account it is usually empty.
    /// </summary>
    public string? PartnerCode { get; set; }

    /// <summary>The other side of the entry, as SAP records it on the line.</summary>
    public string? OffsetAccount { get; set; }

    public string? Description { get; set; }
    public string? Reference { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    /// <summary>The account balance after this line, carried forward from the opening balance.</summary>
    public decimal Balance { get; set; }

    public string? Currency { get; set; }
    public string? CreatedBy { get; set; }
}
