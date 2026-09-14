using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace ShopInventory.Web.Components.Layout;

/// <summary>
/// The sidebar's Depots &amp; vendors link: lit on /vending and on every vendor's page under it, but not
/// on /vending/sales, which has a link of its own beside it.
/// </summary>
/// <remarks>
/// A prefix match on /vending takes in /vending/sales, and an exact one drops /vending/vendors/{id}; the
/// sidebar should light exactly one of the pair either way.
/// </remarks>
public sealed class VendingDepotsNavLink : NavLink
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    protected override bool ShouldMatch(string uriAbsolute)
    {
        if (!base.ShouldMatch(uriAbsolute))
        {
            return false;
        }

        var path = "/" + Navigation.ToBaseRelativePath(uriAbsolute).Split('?', '#')[0].TrimEnd('/');
        return !path.Equals("/vending/sales", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/vending/sales/", StringComparison.OrdinalIgnoreCase);
    }
}
