namespace ShopInventory.DTOs;

/// <summary>
/// The vending operation on one page: the depot cashier accounts that sell to vendors, and the vendors
/// they sell to.
/// </summary>
public sealed class VendingOverviewDto
{
    /// <summary>One row per business partner a vending account sells on — a depot.</summary>
    public List<VendingDepotDto> Depots { get; set; } = [];

    public List<VendingAccountDto> Accounts { get; set; } = [];

    /// <summary>
    /// Every vendor, active or not, under a business partner that a vending account sells on. Removed
    /// vendors are included because this is the page that restores them.
    /// </summary>
    public List<RouteCustomerDto> Vendors { get; set; } = [];
}

/// <summary>
/// A vending depot: the business partner its cashiers invoice under, the warehouse they draw from and
/// the vendors they serve.
/// </summary>
/// <remarks>
/// Not a table. A depot is its business partner, and every cashier at it carries that partner, the
/// warehouse and the cost centre on their own account. Several cashiers may work one depot, so the
/// warehouses and cost centres are lists — and more than one entry in either is exactly the drift this
/// page is here to catch.
/// </remarks>
public sealed class VendingDepotDto
{
    public string BusinessPartnerCode { get; set; } = string.Empty;

    public List<string> WarehouseCodes { get; set; } = [];

    public List<string> CostCentreCodes { get; set; } = [];

    public int CashierCount { get; set; }

    public int ActiveCashierCount { get; set; }

    public int VendorCount { get; set; }

    public int ActiveVendorCount { get; set; }

    /// <summary>Why this depot's cashiers do not agree, or null when they do.</summary>
    public string? SetupProblem { get; set; }

    /// <summary>
    /// The prefix this depot's vendor codes start with — VMB, VMP or VMM, from its warehouse — or null
    /// when its warehouses give it none, in which case <see cref="VendorCodeProblem"/> says why.
    /// </summary>
    public string? VendorCodePrefix { get; set; }

    /// <summary>The code a vendor added now without one would take, or null when none can be issued.</summary>
    public string? NextVendorCode { get; set; }

    /// <summary>Why vendors cannot be numbered at this depot, or null when they can.</summary>
    public string? VendorCodeProblem { get; set; }
}

/// <summary>
/// A <c>CartVendor</c> account — a depot cashier invoicing vendors — and the three assignments every one
/// of its sales is made on.
/// </summary>
public sealed class VendingAccountDto
{
    public Guid UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    public string? FullName { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Who its sales are invoiced to, and whose vendor list it sells from.</summary>
    public string? BusinessPartnerCode { get; set; }

    public string? CostCentreCode { get; set; }

    /// <summary>The warehouse it draws stock from. Null when the account has none, or more than one.</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>Active vendors under <see cref="BusinessPartnerCode"/> — the length of its picker on the till.</summary>
    public int ActiveVendorCount { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// What stops this account selling, in the words the till would refuse with, or null when nothing
    /// does. Said here so a misconfigured account is found on this page rather than at the counter.
    /// </summary>
    public string? SetupProblem { get; set; }
}
