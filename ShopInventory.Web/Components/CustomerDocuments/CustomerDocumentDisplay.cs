using System.Globalization;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.CustomerDocuments;

/// <summary>
/// How a WhatsApp delivery's state reads on screen — one wording and one colour per state, shared by
/// the invoice drawer and the administrators' log so the two never describe one send differently.
/// </summary>
internal static class CustomerDocumentDisplay
{
    public static string StatusLabel(string? status) => status switch
    {
        "Pending" => "Queued",
        "Preparing" => "Preparing",
        "WaitingForFiscal" => "Waiting for fiscal receipt",
        "Sending" => "Sending",
        "Sent" => "Sent",
        "SentUnconfirmed" => "Sent (unconfirmed)",
        "Uncertain" => "Checking whether it went",
        "NotOnWhatsApp" => "Not on WhatsApp",
        "Held" => "Needs attention",
        "Failed" => "Failed",
        "Cancelled" => "Withdrawn",
        "Skipped" => "Skipped",
        _ => status ?? "—"
    };

    /// <summary>
    /// The state's Nocturne family — good, info, warn, bad or neutral. One answer for the chip on a
    /// row and the swatch in the status filter, so the colour someone filters by is the colour they see.
    /// </summary>
    public static string StatusTone(string? status) => status switch
    {
        "Sent" or "SentUnconfirmed" => "good",
        "Pending" or "Preparing" or "WaitingForFiscal" or "Sending" or "Uncertain" => "info",
        "Held" or "NotOnWhatsApp" => "warn",
        "Failed" => "bad",
        _ => "neutral"
    };

    public static string TriggerLabel(string? trigger) => trigger switch
    {
        "Auto" => "Automatic",
        "Manual" => "Sent by staff",
        // A number the customer gave at the sale itself: at the van today, at a till counter later.
        "Counter" => "Given at the sale",
        _ => trigger ?? "—"
    };

    public static string Stamp(DateTime? utc) => utc is { } value
        ? IAuditService.ToCAT(value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)
        : "—";

    public static string Money(decimal? total, string? currency) => total is { } value
        ? $"{(string.IsNullOrWhiteSpace(currency) || currency == "##" ? "$" : currency)} {value.ToString("N2", CultureInfo.InvariantCulture)}"
        : "—";
}
