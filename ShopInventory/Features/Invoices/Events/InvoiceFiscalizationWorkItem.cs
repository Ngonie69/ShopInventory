using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Events;

/// <summary>An invoice just posted to SAP, queued to be fiscalised off the request path.</summary>
/// <param name="InitiatedByUserId">
/// The user who raised the invoice. Besides being notified, they are named to the fiscalisation platform as the
/// receipt's operator, so queue only an invoice this user raised.
/// </param>
/// <param name="InitiatedByUsername">That user's username, when the id alone may not find them.</param>
public sealed record InvoiceFiscalizationWorkItem(
    InvoiceDto Invoice,
    CustomerFiscalDetails CustomerDetails,
    Guid? InitiatedByUserId,
    string? InitiatedByUsername,
    string? NotificationActionUrl);