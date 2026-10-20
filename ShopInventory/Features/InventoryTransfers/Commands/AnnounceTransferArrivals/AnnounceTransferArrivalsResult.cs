namespace ShopInventory.Features.InventoryTransfers.Commands.AnnounceTransferArrivals;

/// <param name="WarehousesWatched">The till warehouses SAP was asked about.</param>
/// <param name="Arrivals">Transfers into those warehouses that SAP holds for yesterday and today.</param>
/// <param name="Announced">Of those, the ones sent to the tills on this pass.</param>
/// <param name="RecordedSilently">
/// Transfers already in SAP when watching began, recorded on the first pass so they are never announced.
/// </param>
public sealed record AnnounceTransferArrivalsResult(
    int WarehousesWatched,
    int Arrivals,
    int Announced,
    int RecordedSilently);
