namespace ShopInventory.DTOs;

/// <summary>
/// Customers and consolidated groups currently over their SAP credit limit — the same finding the
/// evening review notifies on, readable on demand.
/// </summary>
public class CreditLimitReviewDto
{
    /// <summary>
    /// When the underlying SAP sweep ran. Not the time of the request: a result served from cache
    /// can be several minutes old, and a credit decision should be made knowing which.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>True when this answer came from cache rather than a fresh SAP sweep.</summary>
    public bool FromCache { get; set; }

    /// <summary>Customers read from SAP.</summary>
    public int CustomersRead { get; set; }

    /// <summary>Accounts and groups that hold a limit, so had something to measure.</summary>
    public int LimitsMeasured { get; set; }

    public int BreachCount { get; set; }

    /// <summary>Total by which the listed accounts and groups exceed their limits.</summary>
    public decimal TotalOver { get; set; }

    /// <summary>Worst first.</summary>
    public List<CreditLimitBreachDto> Breaches { get; set; } = new();
}

public class CreditLimitBreachDto
{
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public string? Currency { get; set; }

    /// <summary>
    /// True when this row is a consolidated group measured against the parent's limit, in which
    /// case <see cref="CardCode"/> is the parent and the figures cover every account under it.
    /// </summary>
    public bool IsGroup { get; set; }

    /// <summary>Accounts included — 1 for a standalone account.</summary>
    public int AccountCount { get; set; }

    public decimal CreditLimit { get; set; }
    public decimal Balance { get; set; }
    public decimal OpenOrders { get; set; }
    public decimal Exposure { get; set; }
    public decimal AmountOver { get; set; }
}
