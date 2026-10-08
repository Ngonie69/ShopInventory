using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// What a person may do with a delivery in each state — the one rule the API enforces and the web
/// offers buttons by.
/// </summary>
internal static class CustomerDocumentDeliveryRules
{
    /// <summary>States the job still acts on. Sending one again would race the job.</summary>
    public static bool IsInFlight(CustomerDocumentDeliveryStatus status) => status is
        CustomerDocumentDeliveryStatus.Pending or
        CustomerDocumentDeliveryStatus.Preparing or
        CustomerDocumentDeliveryStatus.WaitingForFiscal or
        CustomerDocumentDeliveryStatus.Sending;

    public static bool CanRetry(CustomerDocumentDeliveryStatus status) => !IsInFlight(status);

    /// <summary>
    /// The states in which the customer may well already hold the document, so a resend asks the
    /// sender to say they do not.
    /// </summary>
    public static bool RetryNeedsConfirmation(CustomerDocumentDeliveryStatus status) => status is
        CustomerDocumentDeliveryStatus.Sent or
        CustomerDocumentDeliveryStatus.SentUnconfirmed or
        CustomerDocumentDeliveryStatus.Uncertain;

    /// <summary>Withdrawable only while nothing has been handed to the gateway.</summary>
    public static bool CanCancel(CustomerDocumentDeliveryStatus status) => status is
        CustomerDocumentDeliveryStatus.Pending or
        CustomerDocumentDeliveryStatus.WaitingForFiscal or
        CustomerDocumentDeliveryStatus.Held;

    /// <summary>States the job no longer acts on, and the row is closed in.</summary>
    public static bool IsClosed(CustomerDocumentDeliveryStatus status) => status is
        CustomerDocumentDeliveryStatus.Sent or
        CustomerDocumentDeliveryStatus.SentUnconfirmed or
        CustomerDocumentDeliveryStatus.NotOnWhatsApp or
        CustomerDocumentDeliveryStatus.Failed or
        CustomerDocumentDeliveryStatus.Cancelled or
        CustomerDocumentDeliveryStatus.Skipped;

    /// <summary>The column's width, so an overlong gateway message is what gets cut, never the save.</summary>
    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
