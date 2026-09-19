using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Hubs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>
/// Tells the tills in a sale's warehouse that ZIMRA has accepted a credit against it.
/// </summary>
public interface IDesktopCreditTillNotifier
{
    /// <summary>Never throws: the credit is filed whether or not a till hears about it.</summary>
    Task NotifyIssuedAsync(Guid creditNoteId, CancellationToken cancellationToken);
}

/// <summary>
/// Pushes <c>DesktopCreditIssued</c> to <c>warehouse:{CODE}</c> on the notification hub — the same
/// group and connection the till already uses for <c>InvoiceCancelled</c>.
/// </summary>
/// <remarks>
/// <para>
/// A credit is raised on the web, from the sale's page, and until this the till that rang the sale
/// up was never told. Its own history still showed the sale in full, and the operator learnt of the
/// return only if someone walked over and said so.
/// </para>
/// <para>
/// Sent once, when the credit first becomes Fiscalised — a credit the device refused, or one whose
/// outcome is unknown, is not a credit yet and the till is not told about it. SignalR keeps no
/// backlog, so a till that is closed or offline at that moment misses the alert; the credit itself
/// is on the Desktop Credit Notes page either way.
/// </para>
/// </remarks>
public sealed class HubDesktopCreditTillNotifier(
    ApplicationDbContext db,
    IHubContext<NotificationHub> hub,
    ILogger<HubDesktopCreditTillNotifier> logger) : IDesktopCreditTillNotifier
{
    public const string MethodName = "DesktopCreditIssued";

    public async Task NotifyIssuedAsync(Guid creditNoteId, CancellationToken cancellationToken)
    {
        try
        {
            var credit = await db.DesktopCreditNotes.AsNoTracking()
                .Where(n => n.Id == creditNoteId && n.Status == DesktopCreditStatuses.Fiscalised)
                .Select(n => new
                {
                    n.Id,
                    n.Number,
                    n.SaleId,
                    n.Amount,
                    n.Currency,
                    n.Reason,
                    n.FiscalisedAtUtc,
                    n.Sale.ExternalReferenceId,
                    n.Sale.WarehouseCode,
                    n.Sale.CardName,
                    n.Sale.RouteCustomerName
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (credit is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(credit.WarehouseCode))
            {
                logger.LogWarning(
                    "Credit {CreditNote} is fiscalised but its sale names no warehouse; no till was told.",
                    credit.Number);
                return;
            }

            var payload = new DesktopCreditIssuedNotice(
                credit.Id,
                credit.Number,
                // What a till matches against its own records: the reference it sent with the sale,
                // and the INV number it printed on the customer's receipt.
                credit.ExternalReferenceId,
                DesktopSaleNumber.Format(credit.SaleId),
                credit.WarehouseCode.Trim().ToUpperInvariant(),
                credit.Amount,
                credit.Currency,
                credit.Reason,
                string.IsNullOrWhiteSpace(credit.RouteCustomerName) ? credit.CardName : credit.RouteCustomerName,
                credit.FiscalisedAtUtc ?? DateTime.UtcNow);

            await hub.Clients
                .Group(NotificationHub.WarehouseGroup(payload.WarehouseCode))
                .SendAsync(MethodName, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell the tills about credit {CreditNoteId}.", creditNoteId);
        }
    }
}

/// <summary>What the till receives. The till's <c>DesktopCreditIssuedNotice</c> mirrors it by name.</summary>
public sealed record DesktopCreditIssuedNotice(
    Guid CreditNoteId,
    string CreditNoteNumber,
    string SaleReference,
    string SaleNumber,
    string WarehouseCode,
    decimal Amount,
    string Currency,
    string Reason,
    string? CustomerName,
    DateTime FiscalisedAtUtc);
