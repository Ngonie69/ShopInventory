namespace ShopInventory.Web.Data;

/// <summary>
/// One change a person made to the delivery routes, held as a delta against the
/// generated catalogue rather than a copy of it.
///
/// The routes workbook stays the authority for what the routes are — it is
/// compiled into DeliveryRoutes.g.cs by
/// scripts/DeliveryRoutes/generate_delivery_routes.py. Storing whole
/// assignments here instead would mean the next regeneration either wiped
/// everyone's corrections or silently diverged from the sheet. A delta survives
/// the regeneration, and it also answers the question the report actually gets
/// asked: which of these shops are where the sheet put them, and which did we
/// move?
///
/// One row per (business partner, route). <see cref="IsRemoval"/> says which
/// direction: false adds the shop to that route, true takes it off a route the
/// workbook put it on. Moving a shop between routes is therefore two rows.
/// </summary>
public class RouteAssignmentOverride
{
    public int Id { get; set; }

    /// <summary>
    /// The SAP business partner code, exactly as the partner master holds it —
    /// the currency suffix is part of the key ("SPA059 USD").
    /// </summary>
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// Held alongside the code so the admin page and the audit trail can name
    /// the shop without a round trip, and can still name it if the partner is
    /// later archived out of the cache.
    /// </summary>
    public string? CardName { get; set; }

    /// <summary>
    /// The route this row adds the shop to, or removes it from. Matches a
    /// <see cref="ShopInventory.Web.Common.DeliveryRoute.Name"/>; a row naming a
    /// route the catalogue no longer defines is ignored rather than failing the
    /// page, so a workbook change cannot break the report.
    /// </summary>
    public string RouteName { get; set; } = string.Empty;

    /// <summary>
    /// false: put this shop on the route. true: take it off a route the
    /// workbook assigns it to.
    /// </summary>
    public bool IsRemoval { get; set; }

    /// <summary>
    /// Why the change was made. Worth capturing — a reassignment is a claim
    /// about which truck calls where, and the next person needs the reason.
    /// </summary>
    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }
}
