using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Who a till sells as, and where the stock comes from.
/// </summary>
/// <param name="UserId">The signed-in account. Recorded on the sale so takings can be attributed.</param>
/// <param name="CardCode">The business partner the sale is invoiced to.</param>
/// <param name="WarehouseCode">The warehouse the stock leaves. One per business partner.</param>
/// <param name="CostCentreCode">The cost centre to book against, when the account carries one.</param>
public sealed record SellingAccountAssignments(
    Guid UserId,
    string CardCode,
    string WarehouseCode,
    string? CostCentreCode);

/// <summary>
/// Resolves the account a sale is made on behalf of.
///
/// A till used to say who it was selling as in the request body, and nothing checked it against the
/// account that signed in — so any authenticated till could sell from any warehouse as any customer.
/// These three values are pre-linked to the account instead, and this is the one place they are read.
/// </summary>
public static class SellingAccountResolver
{
    /// <summary>
    /// Reads the selling identity off the account, or explains which assignment is missing.
    /// </summary>
    /// <remarks>
    /// Deliberately not gated on a role predicate. <see cref="ApplicationRoles.RequiresAssignedBusinessPartnerCode"/>
    /// and its siblings all delegate to the legacy van-sales role set (ADR and Sales), so for a Cashier
    /// they answer "not required" and the check would pass an unassigned account straight through.
    /// Selling is the whole purpose of the endpoint this guards, so the requirement is unconditional.
    ///
    /// An account assigned a shop takes all three values from it and none from its own columns. The
    /// shop is preferred rather than merged because merging would need a rule for each disagreement,
    /// and there is no reading under which an account should sell as one business partner out of a
    /// warehouse belonging to another. Callers must have loaded <see cref="User.Shop"/>; a
    /// <see cref="User.ShopId"/> set with no shop attached is treated as an unloaded query rather than
    /// quietly falling back to the account's own columns, which would sell on stale values.
    /// </remarks>
    public static ErrorOr<SellingAccountAssignments> Resolve(User? user)
    {
        if (user is null || !user.IsActive)
        {
            return Errors.Errors.DesktopSales.Unauthenticated;
        }

        if (user.ShopId is not null)
        {
            return ResolveFromShop(user);
        }

        var cardCode = user.AssignedBusinessPartnerCode?.Trim();
        if (string.IsNullOrWhiteSpace(cardCode))
        {
            return Errors.Errors.DesktopSales.MissingCustomerAssignment;
        }

        // Not VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode: that takes the first entry of
        // the list, which for an account holding two would pick one on JSON array order and sell from
        // it silently. A business partner draws from exactly one warehouse, so more than one assigned
        // is a configuration error worth surfacing rather than resolving.
        var warehouseCodes = user.GetWarehouseCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (warehouseCodes.Count == 0)
        {
            return Errors.Errors.DesktopSales.MissingWarehouseAssignment;
        }

        if (warehouseCodes.Count > 1)
        {
            return Errors.Errors.DesktopSales.AmbiguousWarehouseAssignment(warehouseCodes.Count);
        }

        // Carried when the account has one, not required. The cost centre is a reporting dimension
        // that SAP will default if it is absent, so demanding it would stop an otherwise correctly
        // configured till from selling in exchange for nothing that affects the money.
        var costCentreCode = VanSalesCompatibilityMapper.ResolveAssignedCostCentreCode(user);

        return new SellingAccountAssignments(user.Id, cardCode, warehouseCodes[0], costCentreCode);
    }

    /// <summary>
    /// Reads the selling identity off the account's shop.
    /// </summary>
    /// <remarks>
    /// No ambiguity check here, unlike the per-account path above: a shop carries exactly one
    /// warehouse column, so there is no list to disagree with itself. The cost centre stays optional
    /// for the same reason it is optional on an account — SAP defaults it, so demanding it would stop
    /// a correctly configured shop trading over a reporting dimension.
    /// </remarks>
    private static ErrorOr<SellingAccountAssignments> ResolveFromShop(User user)
    {
        var shop = user.Shop;
        if (shop is null)
        {
            // The account names a shop the caller did not load. Selling on the account's own columns
            // instead would silently use values the shop was meant to replace, so this is a fault in
            // the query rather than a configuration problem to report to the operator.
            throw new InvalidOperationException(
                $"User {user.Id} has ShopId {user.ShopId} but Shop was not loaded. " +
                "Include(u => u.Shop) before resolving a selling account.");
        }

        var shopName = string.IsNullOrWhiteSpace(shop.Name) ? shop.Code : shop.Name;

        if (!shop.IsActive)
        {
            return Errors.Errors.DesktopSales.ShopInactive(shopName);
        }

        var cardCode = shop.BusinessPartnerCode?.Trim();
        if (string.IsNullOrWhiteSpace(cardCode))
        {
            return Errors.Errors.DesktopSales.ShopMisconfigured(shopName, "business partner");
        }

        var warehouseCode = shop.WarehouseCode?.Trim();
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Errors.Errors.DesktopSales.ShopMisconfigured(shopName, "warehouse");
        }

        var costCentreCode = string.IsNullOrWhiteSpace(shop.CostCentreCode)
            ? null
            : shop.CostCentreCode.Trim();

        return new SellingAccountAssignments(user.Id, cardCode, warehouseCode, costCentreCode);
    }
}
