namespace ShopInventory.DTOs;

/// <summary>Send a delivered, failed or held document again.</summary>
public sealed class RetryCustomerDocumentDeliveryRequest
{
    /// <summary>
    /// The sender confirms the customer did not receive it. Required when the earlier send may well have
    /// arrived, so a resend is a decision and not a reflex.
    /// </summary>
    public bool ConfirmNotReceived { get; set; }
}
