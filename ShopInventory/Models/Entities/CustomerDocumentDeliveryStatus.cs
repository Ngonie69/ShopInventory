namespace ShopInventory.Models.Entities;

/// <summary>
/// Where a customer document delivery stands.
/// </summary>
/// <remarks>
/// The states that matter are the ones around the send itself, because a WhatsApp message cannot be
/// taken back. <see cref="Sending"/> is saved before the gateway is called, so a process that dies
/// mid-send leaves a row that says so rather than one that looks unsent and goes out again. Anything
/// whose outcome is not known for certain becomes <see cref="Uncertain"/> and is settled from the
/// gateway's own log, never by sending again.
/// </remarks>
public enum CustomerDocumentDeliveryStatus
{
    /// <summary>Waiting for the delivery job.</summary>
    Pending = 0,

    /// <summary>Claimed by a pass that is preparing the document. Nothing has been sent.</summary>
    Preparing = 1,

    /// <summary>The document has no verified fiscal receipt yet; checked again later.</summary>
    WaitingForFiscal = 2,

    /// <summary>Handed to the gateway. Saved before the call, so a pass that dies here leaves it visible.</summary>
    Sending = 3,

    /// <summary>WhatsApp accepted it and returned a message id.</summary>
    Sent = 4,

    /// <summary>WhatsApp accepted it but returned no message id. Very likely delivered; never retried.</summary>
    SentUnconfirmed = 5,

    /// <summary>The outcome is not known — the answer was lost. Settled from the gateway's log.</summary>
    Uncertain = 6,

    /// <summary>The number has no WhatsApp account.</summary>
    NotOnWhatsApp = 7,

    /// <summary>Needs a person: the fiscal receipt could not be verified, or never arrived.</summary>
    Held = 8,

    /// <summary>It did not go and will not be tried again on its own.</summary>
    Failed = 9,

    /// <summary>Withdrawn by a person, or because the customer opted out first.</summary>
    Cancelled = 10,

    /// <summary>Recorded for a customer who asked for their invoices, but deliberately not sent.</summary>
    Skipped = 11
}
