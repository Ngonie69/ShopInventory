using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace ShopInventory.Web.Components.Layout;

/// <summary>
/// The sidebar's Depots &amp; vendors link: lit on /vending and on every vendor's page under it, but not
/// on /vending/sales or /vending/reconciliation, which have links of their own beside it.
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
        return !SiblingPages.Any(sibling =>
            path.Equals(sibling, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(sibling + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The pages under /vending that have a sidebar link of their own.</summary>
    private static readonly string[] SiblingPages = ["/vending/sales", "/vending/reconciliation"];
}
