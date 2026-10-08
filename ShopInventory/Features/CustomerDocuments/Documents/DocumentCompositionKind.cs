namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>What preparing a document for sending came to.</summary>
public enum DocumentCompositionKind
{
    /// <summary>Ready to send.</summary>
    Ready = 0,

    /// <summary>No verified fiscal receipt yet; look again later.</summary>
    WaitForFiscal = 1,

    /// <summary>Something only a person can settle, such as a receipt that belongs to another document.</summary>
    Hold = 2,

    /// <summary>The document should no longer go — cancelled or reposted in SAP.</summary>
    Cancel = 3,

    /// <summary>It can never be sent as it stands.</summary>
    Fail = 4,

    /// <summary>A passing fault, such as SAP not answering; try again shortly.</summary>
    Retry = 5
}
