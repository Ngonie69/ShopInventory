namespace ShopInventory.Web.Models;

/// <summary>
/// A van sales customer's ordering-app sign-in, as the back office sees it.
/// </summary>
/// <remarks>
/// Mirrors the API's <c>VanSalesCustomerAccountResult</c>. It carries no code and no token by
/// design — the API never returns either, and a model with somewhere to put them invites a future
/// change that does.
/// </remarks>
public class VanSalesCustomerAccountModel
{
    public int Id { get; set; }

    public int RouteCustomerId { get; set; }

    public string RouteCustomerCode { get; set; } = string.Empty;

    public string RouteCustomerName { get; set; } = string.Empty;

    /// <summary>The handset's number in E.164, as the API normalised it.</summary>
    public string PhoneE164 { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public bool IsActive { get; set; }

    /// <summary>
    /// Whether too many wrong codes have locked the account, computed by the API against the clock
    /// at read time rather than stored.
    /// </summary>
    public bool IsLockedOut { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>What the operator fills in to give a shop a sign-in.</summary>
/// <remarks>
/// The phone goes up in whatever form it was typed. Normalising to E.164 is the API's job and it
/// already does it — doing it here as well would give the two a chance to disagree about which
/// forms are acceptable, and the operator would meet the stricter of them without being told why.
/// </remarks>
public class OnboardVanSalesCustomerAccountModel
{
    public int RouteCustomerId { get; set; }

    public string PhoneNumber { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

/// <summary>The accounts screen's whole payload: the sign-ins, and the shops one can be given to.</summary>
public class VanSalesCustomerAccountsViewModel
{
    public List<VanSalesCustomerAccountModel> Accounts { get; set; } = new();

    public List<RouteCustomerModel> RouteCustomers { get; set; } = new();
}
