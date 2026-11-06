using System.Text.RegularExpressions;
using ShopInventory.Models;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// A transfer request does not post to SAP until someone who runs each warehouse it delivers to has
/// agreed to take the stock.
/// </summary>
/// <remarks>
/// <para>The approval engine adds this stage to every transfer request's approval, after whatever
/// stages its template routed it to. It is one stage definition, seeded with a fixed id, so an
/// administrator can change who holds it or switch it off (deactivate it) on the Approval Process page
/// without a deploy. Its warehouse scope is always the destination, whatever the definition says,
/// because that is the whole of what it is for.</para>
///
/// <para>Two kinds of destination never wait for it:</para>
/// <list type="bullet">
/// <item>Vans. A van's stock request is raised by the rep who drives it, so the person "receiving"
/// the stock is the person who asked for it.</item>
/// <item>A warehouse the requester runs themselves. Asking for stock to be sent to your own
/// warehouse is the agreement to take it, so their request counts as that warehouse's approval.
/// The engine never lets anyone approve their own request, so without this every such request
/// would wait for a second person at the same warehouse, or an administrator.</item>
/// </list>
///
/// <para>The owner of a warehouse is anyone whose account has it assigned and who holds one of the
/// stage's roles. A warehouse nobody is assigned to can still be approved by an administrator.</para>
/// </remarks>
public static partial class ReceivingWarehouseOwnerRule
{
    /// <summary>The stage definition the rule reads its roles, approvals and on/off switch from.</summary>
    public static readonly Guid StageId = Guid.Parse("11000000-0000-0000-0000-000000000005");

    public const string StageName = "Receiving warehouse owner";

    public const string StageDescription =
        "Someone assigned to each warehouse the request delivers to agrees to take the stock. " +
        "Vans are left out, and a warehouse the requester runs counts as approved by their request.";

    /// <summary>The roles that run a warehouse. Sales roles run vans, which the rule leaves out.</summary>
    public static readonly string[] DefaultRoles =
    [
        ApplicationRoles.StockController,
        ApplicationRoles.DepotController,
        ApplicationRoles.WashBay,
        ApplicationRoles.DispatchOfficer,
        ApplicationRoles.ProductionOfficer,
        ApplicationRoles.DistributionOfficer
    ];

    /// <summary>
    /// Whether <paramref name="warehouseCode"/> is a van. SAP names them VAN001, VAN002 and so on; the
    /// pattern rather than a list, so a new van is a van the day it is added.
    /// </summary>
    public static bool IsVanWarehouse(string? warehouseCode)
        => warehouseCode is not null && VanWarehouseCode().IsMatch(warehouseCode.Trim());

    /// <summary>The destinations whose owner must agree: every one but the vans.</summary>
    public static List<string> WarehousesToApprove(IEnumerable<string> destinations)
        => destinations
            .Where(code => !string.IsNullOrWhiteSpace(code) && !IsVanWarehouse(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The warehouses among <paramref name="warehouses"/> that <paramref name="user"/> runs: assigned to
    /// their account, and they hold the stage.
    /// </summary>
    /// <remarks>
    /// <para>Two things follow from running a receiving warehouse. A request its owner raised is their
    /// agreement to take the stock. And an owner who approves any stage of a request has agreed to it,
    /// so they are not asked a second time as the owner.</para>
    ///
    /// <para>An administrator runs none of them by role alone. They may approve the stage for any
    /// warehouse, but they do it on the stage itself: approving the administrator review, or raising a
    /// request, never signs for a warehouse on its owner's behalf without their saying so.</para>
    /// </remarks>
    public static List<string> OwnedBy(
        User? user,
        IReadOnlyCollection<string> authorizerRoles,
        IReadOnlyCollection<Guid> authorizerUserIds,
        IEnumerable<string> warehouses)
    {
        if (user is null || !user.IsActive)
            return [];

        var holdsStage = authorizerUserIds.Contains(user.Id) ||
                         authorizerRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);
        if (!holdsStage)
            return [];

        var assigned = user.GetWarehouseCodes();
        return warehouses
            .Where(code => assigned.Contains(code, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    [GeneratedRegex("^VAN[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VanWarehouseCode();
}
