namespace ShopInventory.Web.Models;

/// <summary>
/// Mirror of the API's <c>CreditHeadroomResponseDto</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, as the rest of this project's models are — the two apps share no assembly.
/// Nullability has to match the API's DTO exactly or System.Text.Json throws on deserialize and the
/// page reports no data.
/// </remarks>
public class CreditHeadroomResponse
{
    public DateTime GeneratedAt { get; set; }
    public bool FromCache { get; set; }
    public List<CreditHeadroom> Accounts { get; set; } = new();
}

/// <summary>How much room one customer has left against the limit that governs it.</summary>
public class CreditHeadroom
{
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// False when neither this account nor a parent holds a limit. Everything below is meaningless
    /// then, and it must not be shown as "no room left" — it is the opposite.
    /// </summary>
    public bool HasLimit { get; set; }

    /// <summary>
    /// The account whose limit governs — itself, or the parent it consolidates into. A payment
    /// against any other account will not move these figures.
    /// </summary>
    public string? CreditAccountCardCode { get; set; }

    public string? CreditAccountName { get; set; }
    public string? Currency { get; set; }
    public bool IsGroup { get; set; }
    public int AccountCount { get; set; }
    public decimal CreditLimit { get; set; }

    /// <summary>What is already owed, before the order being considered.</summary>
    public decimal Exposure { get; set; }

    /// <summary>What an order can be worth before this account goes over. Negative when already over.</summary>
    public decimal Headroom { get; set; }
}
