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
    /// </remarks>
    public static ErrorOr<SellingAccountAssignments> Resolve(User? user)
    {
        if (user is null || !user.IsActive)
        {
            return Errors.Errors.DesktopSales.Unauthenticated;
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
}
