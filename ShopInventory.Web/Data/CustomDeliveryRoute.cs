namespace ShopInventory.Web.Data;

/// <summary>
/// A delivery route somebody added, rather than one the routes workbook
/// defines.
///
/// The workbook is regenerated into DeliveryRoutes.g.cs whenever the routes
/// change, so a route added here has to live outside it for the same reason a
/// reassignment does — see <see cref="RouteAssignmentOverride"/>. Shops get onto
/// one of these through that same override table; this row only declares that
/// the route exists, and when it runs.
/// </summary>
public class CustomDeliveryRoute
{
    public int Id { get; set; }

    /// <summary>
    /// What the route is called. Unique, and cannot take the name of a route the
    /// workbook already defines — two routes with one name would be one route
    /// with contradictory stops.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The days it runs, comma separated ("Tuesday", or "Monday,Friday" for a
    /// route that runs twice). Held as text to match the generated catalogue,
    /// which takes them from the workbook's own day headings.
    /// </summary>
    public string? Days { get; set; }

    /// <summary>Truck allocation, as the workbook writes it — "10T", "24T".</summary>
    public string? Truck { get; set; }

    /// <summary>Why the route was added.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }
}
