namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>The outcome of preparing a document: the document itself, or why there is none.</summary>
public sealed record DocumentComposition(
    DocumentCompositionKind Kind,
    ComposedCustomerDocument? Document,
    string? Reason)
{
    public static DocumentComposition Ready(ComposedCustomerDocument document) => new(DocumentCompositionKind.Ready, document, null);

    public static DocumentComposition WaitForFiscal(string reason) => new(DocumentCompositionKind.WaitForFiscal, null, reason);

    public static DocumentComposition Hold(string reason) => new(DocumentCompositionKind.Hold, null, reason);

    public static DocumentComposition Cancel(string reason) => new(DocumentCompositionKind.Cancel, null, reason);

    public static DocumentComposition Fail(string reason) => new(DocumentCompositionKind.Fail, null, reason);

    public static DocumentComposition Retry(string reason) => new(DocumentCompositionKind.Retry, null, reason);
}
