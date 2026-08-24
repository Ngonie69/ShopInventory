namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>How a one-time code reached the customer, if it did.</summary>
public enum OtpDeliveryChannel
{
    /// <summary>Nothing carried it. The customer is waiting for a message that will not arrive.</summary>
    None = 0,

    /// <summary>Sent through the OpenWA gateway.</summary>
    WhatsApp = 1,

    /// <summary>Written to the log for local development only, never in production.</summary>
    DevelopmentLog = 2
}

/// <summary>Delivers a one-time code to a customer's phone.</summary>
public interface IVanSalesCustomerOtpSender
{
    /// <summary>
    /// Attempt to deliver <paramref name="code"/> to <paramref name="phoneE164"/>.
    /// </summary>
    /// <remarks>
    /// Reports which channel succeeded rather than throwing on failure. The caller must answer the
    /// customer identically whether or not delivery worked — an error that distinguishes
    /// "sent" from "not sent" also distinguishes "registered" from "not registered", which is the
    /// enumeration hole the whole endpoint is shaped to avoid.
    /// </remarks>
    Task<OtpDeliveryChannel> SendAsync(string phoneE164, string code, CancellationToken cancellationToken);
}
