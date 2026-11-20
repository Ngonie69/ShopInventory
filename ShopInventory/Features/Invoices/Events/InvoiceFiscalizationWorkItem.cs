using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Events;

/// <summary>An invoice just posted to SAP, queued to be fiscalised off the request path.</summary>
/// <param name="Invoice">The invoice as SAP just accepted it.</param>
/// <param name="CustomerDetails">The buyer's details for the receipt.</param>
/// <param name="InitiatedByUserId">
/// The user who raised the invoice. Besides being notified, they are named to the fiscalisation platform as the
/// receipt's operator, so queue only an invoice this user raised.
/// </param>
/// <param name="InitiatedByUsername">That user's username, when the id alone may not find them.</param>
/// <param name="NotificationActionUrl">The page the user's notification opens; the invoices list when blank.</param>
public sealed record InvoiceFiscalizationWorkItem(
    InvoiceDto Invoice,
    CustomerFiscalDetails CustomerDetails,
    Guid? InitiatedByUserId,
    string? InitiatedByUsername,
    string? NotificationActionUrl);