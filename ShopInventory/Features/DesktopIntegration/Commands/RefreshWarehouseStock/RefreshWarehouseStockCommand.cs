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
/// </remarks>
public sealed record RefreshWarehouseStockCommand(string WarehouseCode)
    : IRequest<ErrorOr<RefreshWarehouseStockResult>>;

/// <param name="WarehouseCode">The warehouse refreshed.</param>
/// <param name="LedgerDay">The snapshot day whose rows were moved.</param>
/// <param name="ItemsChecked">Items SAP and the ledger both hold that were compared.</param>
/// <param name="ItemsCorrected">Items whose quantity was moved to SAP's figure.</param>
/// <param name="ItemsAdded">Items SAP holds that today's snapshot had no row for.</param>
/// <param name="RefreshedAt">When the refresh finished, UTC.</param>
public sealed record RefreshWarehouseStockResult(
    string WarehouseCode,
    DateTime LedgerDay,
    int ItemsChecked,
    int ItemsCorrected,
    int ItemsAdded,
    DateTime RefreshedAt);
