namespace ShopInventory.Web.Models;

/// <summary>The web copy of the API's <c>VendingOverviewDto</c>: depots, their cashiers and their vendors.</summary>
public sealed class VendingOverviewModel
{
    public List<VendingDepotModel> Depots { get; set; } = [];

    public List<VendingAccountModel> Accounts { get; set; } = [];

    /// <summary>Every vendor at every depot, removed ones included.</summary>
    public List<RouteCustomerModel> Vendors { get; set; } = [];
}

/// <summary>A vending depot — the business partner its cashiers sell on.</summary>
public sealed class VendingDepotModel
{
    public string BusinessPartnerCode { get; set; } = string.Empty;

    public List<string> WarehouseCodes { get; set; } = [];

    public List<string> CostCentreCodes { get; set; } = [];

    public int CashierCount { get; set; }

    public int ActiveCashierCount { get; set; }

    public int VendorCount { get; set; }

    public int ActiveVendorCount { get; set; }

    public string? SetupProblem { get; set; }

    /// <summary>VMB, VMP or VMM — from the depot's warehouse. Null when it has none.</summary>
    public string? VendorCodePrefix { get; set; }

    /// <summary>The code a vendor added now without one would take.</summary>
    public string? NextVendorCode { get; set; }

    /// <summary>Why vendors cannot be numbered at this depot, or null when they can.</summary>
    public string? VendorCodeProblem { get; set; }
}

/// <summary>A depot cashier's <c>CartVendor</c> account.</summary>
public sealed class VendingAccountModel
{
    public Guid UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    public string? FullName { get; set; }

    public bool IsActive { get; set; }

    public string? BusinessPartnerCode { get; set; }

    public string? CostCentreCode { get; set; }

    public string? WarehouseCode { get; set; }

    public int ActiveVendorCount { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public string? SetupProblem { get; set; }
}
