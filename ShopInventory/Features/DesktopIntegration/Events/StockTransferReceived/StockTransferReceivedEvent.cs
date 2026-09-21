using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Events.StockTransferReceived;

/// <summary>
/// One line of a SAP stock transfer has landed in a monitored warehouse and is on the day's ledger.
/// </summary>
/// <remarks>
/// <para>Raised by the transfer listener's webhook for the inbound side of a transfer only, and only
/// the first time a line is seen: a re-delivered line is journalled as a duplicate and raises nothing,
/// so a subscriber may treat every event as stock that has just arrived. It says what landed and
/// where; who needs to know is the subscriber's question. The van sales feature answers it for a
/// warehouse a van drives — see <c>StockTransferReceivedHandler</c> there.</para>
///
/// <para>Published after the ledger write is saved, never before. An event that said stock had
/// arrived while the row could still fail to commit would send a handset to read a figure that was
/// not there.</para>
/// </remarks>
public sealed record StockTransferReceivedEvent(
    string WarehouseCode,
    string SourceWarehouse,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    int? TransferDocEntry,
    int? TransferDocNum,
    DateTime ReceivedAtUtc) : INotification;
