using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// What Create Invoice keeps of a half-entered invoice across a lost circuit. See <see cref="FormDraft"/>.
/// </summary>
/// <remarks>
/// The request keeps its <see cref="CreateInvoiceRequest.ClientRequestId"/>, so a resubmit after a
/// reload mid-post is replayed by the API rather than posted a second time.
/// </remarks>
public sealed class InvoiceDraft
{
    public const string Form = "invoice";

    public CreateInvoiceRequest Invoice { get; set; } = new();

    public string? WarehouseCode { get; set; }

    public string? DefaultCostCentre { get; set; }

    public bool NotifyCustomerByEmail { get; set; } = true;

    /// <summary>
    /// True when the form holds nothing worth bringing back: no customer, no lines, no reference,
    /// remarks or crates. An empty form clears the stored draft rather than saving one.
    /// </summary>
    public static bool IsEmpty(CreateInvoiceRequest invoice)
        => string.IsNullOrWhiteSpace(invoice.CardCode)
            && invoice.Lines.Count == 0
            && string.IsNullOrWhiteSpace(invoice.NumAtCard)
            && string.IsNullOrWhiteSpace(invoice.Comments)
            && invoice.CrateQuantity is null;
}
