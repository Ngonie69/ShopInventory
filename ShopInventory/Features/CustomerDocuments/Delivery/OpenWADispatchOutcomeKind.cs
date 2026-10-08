namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>What a call to OpenWA's send-document came to, as far as can be known.</summary>
public enum OpenWADispatchOutcomeKind
{
    /// <summary>WhatsApp accepted it and gave a message id.</summary>
    Sent = 0,

    /// <summary>WhatsApp accepted it without a message id. Very likely delivered.</summary>
    SentUnconfirmed = 1,

    /// <summary>Provably never left — the session was down, the gateway refused to take it, or it could not be reached. Safe to retry.</summary>
    NotSent = 2,

    /// <summary>The gateway refused this document for good, such as one too large. Not retried.</summary>
    Rejected = 3,

    /// <summary>The gateway refused this server — bad key, missing session. Every send would fail the same way.</summary>
    Paused = 4,

    /// <summary>The answer was lost after the request may have reached WhatsApp. Never retried blind.</summary>
    Uncertain = 5
}
