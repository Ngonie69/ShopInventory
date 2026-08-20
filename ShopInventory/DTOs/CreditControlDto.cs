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

/// <summary>
/// How much room a set of customers has left against the limits that govern them.
/// </summary>
public class CreditHeadroomResponseDto
{
    /// <summary>When the underlying SAP sweep ran, not when this was asked.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>True when this answer came from cache rather than a fresh SAP sweep.</summary>
    public bool FromCache { get; set; }

    public List<CreditHeadroomDto> Accounts { get; set; } = new();
}

/// <summary>
/// One customer's room to take on more. Read alongside an order somebody is about to approve.
/// </summary>
public class CreditHeadroomDto
{
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// False when neither this account nor a parent holds a limit. Everything below is meaningless
    /// then, and it must not be read as "no room left" — it is the opposite.
    /// </summary>
    public bool HasLimit { get; set; }

    /// <summary>
    /// The account whose limit governs — itself, or the parent it consolidates into. A payment
    /// against any other account will not move these figures.
    /// </summary>
    public string? CreditAccountCardCode { get; set; }

    public string? CreditAccountName { get; set; }
    public string? Currency { get; set; }

    /// <summary>True when the governing limit is a consolidated group's.</summary>
    public bool IsGroup { get; set; }

    /// <summary>Accounts sharing the governing limit — 1 for a standalone account.</summary>
    public int AccountCount { get; set; }

    public decimal CreditLimit { get; set; }

    /// <summary>What is already owed, before the order being considered.</summary>
    public decimal Exposure { get; set; }

    /// <summary>What an order can be worth before this account goes over. Negative when already over.</summary>
    public decimal Headroom { get; set; }
}
