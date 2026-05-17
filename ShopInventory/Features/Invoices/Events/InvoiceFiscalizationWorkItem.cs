using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Events;

public sealed record InvoiceFiscalizationWorkItem(
    InvoiceDto Invoice,
    CustomerFiscalDetails CustomerDetails);