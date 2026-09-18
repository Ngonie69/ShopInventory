using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The invoice a cashier is part-way through entering, as kept in the browser's localStorage.
/// </summary>
/// <remarks>
/// A Blazor Server page keeps its state in the circuit. When the connection drops long enough for the
/// server to evict the circuit — a deploy, an app-pool recycle, a laptop lid, a flaky link — the
/// reconnect modal can only reload the page, and a half-entered invoice went with it. The page now
/// writes the draft to the browser after every change and reads it back on the next load.
///
/// The draft keeps the request's <see cref="CreateInvoiceRequest.ClientRequestId"/>. If the circuit
/// died while a submit was in flight, the invoice may already be in SAP; resubmitting the restored
/// draft sends the same idempotency key, so the API replays that invoice instead of posting a second.
///
/// The key is per user, so a shared till PC never hands one cashier's basket to the next, and a draft
/// older than <see cref="MaxAge"/> is dropped rather than resurfacing on the next shift.
/// </remarks>
public sealed class InvoiceDraft
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private const string KeyPrefix = "invoice-draft:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DateTimeOffset SavedAt { get; set; }

    public CreateInvoiceRequest Invoice { get; set; } = new();

    public string? WarehouseCode { get; set; }

    public string? DefaultCostCentre { get; set; }

    public bool NotifyCustomerByEmail { get; set; } = true;

    /// <summary>The localStorage key for <paramref name="userKey"/>, or null when there is no user to scope it to.</summary>
    public static string? StorageKey(string? userKey)
        => string.IsNullOrWhiteSpace(userKey) ? null : KeyPrefix + userKey.Trim().ToLowerInvariant();

    /// <summary>
    /// True when the form holds nothing worth bringing back: no customer, no lines, no reference or
    /// remarks. An empty form clears the stored draft rather than saving one.
    /// </summary>
    public static bool IsEmpty(CreateInvoiceRequest invoice)
        => string.IsNullOrWhiteSpace(invoice.CardCode)
            && invoice.Lines.Count == 0
            && string.IsNullOrWhiteSpace(invoice.NumAtCard)
            && string.IsNullOrWhiteSpace(invoice.Comments)
            && invoice.CrateQuantity is null;

    /// <summary>
    /// The JSON to compare against what was last written. It leaves out <see cref="SavedAt"/>, so an
    /// unchanged form produces the same string on every render and nothing is written.
    /// </summary>
    public string Fingerprint()
        => JsonSerializer.Serialize(new { Invoice, WarehouseCode, DefaultCostCentre, NotifyCustomerByEmail }, JsonOptions);

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Reads a stored draft back. Null when there is none, it cannot be read, it is empty, or it is
    /// older than <see cref="MaxAge"/> at <paramref name="now"/>.
    /// </summary>
    public static InvoiceDraft? TryRestore(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        InvoiceDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<InvoiceDraft>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (draft?.Invoice is null)
        {
            return null;
        }

        draft.Invoice.Lines ??= new List<CreateInvoiceLineRequest>();

        if (IsEmpty(draft.Invoice) || now - draft.SavedAt > MaxAge || draft.SavedAt > now.AddMinutes(5))
        {
            return null;
        }

        return draft;
    }
}
