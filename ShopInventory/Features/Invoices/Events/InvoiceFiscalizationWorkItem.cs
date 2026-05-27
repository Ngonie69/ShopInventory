using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Events;

public sealed record InvoiceFiscalizationWorkItem(
    InvoiceDto Invoice,
    CustomerFiscalDetails CustomerDetails,
    Guid? InitiatedByUserId,
    string? InitiatedByUsername,
    string? NotificationActionUrl);