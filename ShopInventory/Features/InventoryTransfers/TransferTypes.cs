using ShopInventory.Features.InventoryTransfers.SapApproval;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// The two kinds of inventory transfer, told apart by the warehouses they touch.
/// </summary>
/// <remarks>
/// <para><b>Depot transfers</b> touch a depot centre or shop (<see cref="FactoryTransfers.FactoryDepartments.Depots"/>)
/// or a van. They go through this app's own approval templates and stages and post to SAP once every
/// stage has approved. The factory shop (KEFSHOP) is not a depot: it follows SAP's approvals (user's
/// call, 2026-11-16).</para>
///
/// <para><b>Factory transfers</b> are everything else. The app's own approval does not apply to them, by
/// decision (2026-11-16): SAP's own transfer approval templates do, exactly as they would for a transfer
/// keyed in the B1 client. On a route one of those templates covers, the transfer is saved to SAP as a
/// draft for a B1 user to add (which is what starts SAP's approval — SAP never starts one for a document
/// the Service Layer sends), and the app adds it once SAP has approved it. On a route no template covers,
/// it posts at once, as B1 would.</para>
///
/// <para>One line touching a depot or a van makes the whole transfer a depot transfer, the rule
/// <see cref="SapTransferApprovalRoutes.ExcludedWarehouse"/> applies to conversions since 2026-11-12.</para>
/// </remarks>
public static class TransferTypes
{
    public const string Depot = "Depot";
    public const string Factory = "Factory";

    /// <summary>
    /// The name of the approval the app records for a factory transfer: settled as it is opened, with
    /// no stages, because SAP's approval is the one that applies.
    /// </summary>
    public const string SapApprovalTemplateName = "SAP approval (factory transfer)";

    /// <summary>
    /// Stands in for a template id on a factory transfer's approval. No template definition carries it,
    /// so the approval process pages never offer it and deleting a template never counts it.
    /// </summary>
    public static readonly Guid SapApprovalTemplateId = Guid.Parse("22000000-0000-0000-0000-0000000000fa");

    /// <summary>The type of a transfer that draws on and delivers to these warehouses.</summary>
    public static string Of(IEnumerable<string?> warehouses)
        => SapTransferApprovalRoutes.ExcludedWarehouse(null, null, warehouses) is null ? Factory : Depot;

    /// <summary>The type of a transfer between these header warehouses with these line warehouses.</summary>
    public static string Of(string? fromWarehouse, string? toWarehouse, IEnumerable<string?>? lineWarehouses = null)
        => Of(new[] { fromWarehouse, toWarehouse }.Concat(lineWarehouses ?? []));

    public static bool IsFactory(string? transferType)
        => string.Equals(transferType, Factory, StringComparison.OrdinalIgnoreCase);
}
