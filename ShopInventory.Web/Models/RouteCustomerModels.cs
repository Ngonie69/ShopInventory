namespace ShopInventory.Web.Models;

/// <summary>
/// Which of the two populations sharing the route customer table a read is asking for. Mirrors the
/// API's <c>RouteCustomerScope</c> by name, which is how it is spelt on the query string.
/// </summary>
/// <remarks>
/// A van route is a round a van drives and its route customers are the shops on it. A vending vendor
/// sells from a cart out of a depot and is on no round at all. They are not the same thing, so the
/// route pages ask for <see cref="Route"/> — the default — and the vending pages for
/// <see cref="Vending"/>.
/// </remarks>
public enum RouteCustomerScope
{
    /// <summary>The van routes' shops alone. The default.</summary>
    Route = 0,

    /// <summary>The vending depots' vendors alone.</summary>
    Vending = 1,

    /// <summary>Both.</summary>
    All = 2,
}

public class RouteCustomerModel
{
    public int Id { get; set; }

    public string AssignedBusinessPartnerCode { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The family name, for a cart vendor. Null for a route customer that is a shop.</summary>
    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? VatNumber { get; set; }

    public bool IsActive { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public string? CreatedByUserName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}