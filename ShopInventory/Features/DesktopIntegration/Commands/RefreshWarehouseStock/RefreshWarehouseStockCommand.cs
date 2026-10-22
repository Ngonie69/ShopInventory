using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.RefreshWarehouseStock;

/// <summary>
/// Brings one warehouse's stock ledger back in step with SAP now, rather than waiting for the hourly
/// comparison or the next morning's fetch.
/// </summary>
/// <remarks>
/// For stock SAP received that this system was never told about — a goods receipt PO, most often.
/// The hourly job only asks SAP about items that have already moved today, so an item the shop had
/// at 07:00 and has not sold since stays at its morning figure until tomorrow however much arrives.
/// This asks about every item in the warehouse. It records nothing about the receipt itself: the
/// ledger rows are moved to SAP's figure and no movement or divergence row is written.
///
/// A van is refreshed differently: its row is its morning load plus the day's transfers, so the
/// refresh reads SAP's transfers for the van and applies the ones the ledger has not had, instead of
/// copying SAP's figure. TransferEventListener is not asked or waited for.
/// </remarks>
public sealed record RefreshWarehouseStockCommand(string WarehouseCode)
    : IRequest<ErrorOr<RefreshWarehouseStockResult>>;

/// <param name="WarehouseCode">The warehouse refreshed.</param>
/// <param name="LedgerDay">The snapshot day whose rows were moved.</param>
/// <param name="ItemsChecked">Items SAP and the ledger both hold that were compared.</param>
/// <param name="ItemsCorrected">Items whose quantity was moved to SAP's figure.</param>
/// <param name="ItemsAdded">Items SAP holds that today's snapshot had no row for.</param>
/// <param name="RefreshedAt">When the refresh finished, UTC.</param>
/// <param name="SnapshotRefetched">
/// The day's snapshot had failed or was never fetched, so it was fetched from SAP again instead of
/// corrected. The three counts above are zero then; <paramref name="RowsFetched"/> says what arrived.
/// </param>
/// <param name="RowsFetched">Snapshot rows the fetch wrote, when <paramref name="SnapshotRefetched"/>.</param>
/// <param name="ByTransfer">
/// A van, brought up to date by SAP's transfers rather than by SAP's figure. The three item counts are
/// zero then; the transfer counts below say what happened.
/// </param>
/// <param name="TransfersApplied">Transfer lines applied to the warehouse during this refresh.</param>
/// <param name="TransfersToday">Transfer lines applied to the warehouse on <paramref name="LedgerDay"/>, these included.</param>
/// <param name="TransfersNotApplied">
/// Transfers SAP holds for the warehouse that are not on the ledger and could not be applied this
/// press. Not zero means pressing again, or the listener, still has something to do.
/// </param>
public sealed record RefreshWarehouseStockResult(
    string WarehouseCode,
    DateTime LedgerDay,
    int ItemsChecked,
    int ItemsCorrected,
    int ItemsAdded,
    DateTime RefreshedAt,
    bool SnapshotRefetched = false,
    int RowsFetched = 0,
    bool ByTransfer = false,
    int TransfersApplied = 0,
    int TransfersToday = 0,
    int TransfersNotApplied = 0);
