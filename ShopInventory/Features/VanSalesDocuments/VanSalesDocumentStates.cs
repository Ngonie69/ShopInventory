namespace ShopInventory.Features.VanSalesDocuments;

/// <summary>
/// Where a document the van sales app created stands, with ZIMRA and with SAP.
/// </summary>
/// <remarks>
/// <para><b>Two questions, one answer.</b> A van invoice can be fiscalised and not yet in SAP, in SAP and
/// never fiscalised, both, or neither — and the four mean very different things to whoever is reading the
/// list. A single status column answering both is what lets the page be scanned; the rule that picks it
/// lives here so the list, the counts over it and the filter cannot disagree about one document.</para>
///
/// <para><b>Needs attention outranks everything but done.</b> A document that is both invoiced and
/// fiscalised is finished whatever went wrong on the way. Anything short of that with a failure on it is a
/// person's job, and saying "awaiting SAP" over a sale SAP has refused three times would read as patience
/// being enough.</para>
/// </remarks>
public static class VanSalesDocumentStates
{
    /// <summary>Fiscalised, and in SAP.</summary>
    public const string Complete = "Complete";

    /// <summary>Fiscalised; SAP has not taken it yet.</summary>
    public const string AwaitingSap = "AwaitingSap";

    /// <summary>In SAP, with no fiscal receipt found for it.</summary>
    public const string NotFiscalised = "NotFiscalised";

    /// <summary>Neither yet, and nothing has failed — a queued conversion that has not been reached.</summary>
    public const string InProgress = "InProgress";

    /// <summary>Stopped short of complete with a failure on it.</summary>
    public const string NeedsAttention = "NeedsAttention";

    public static readonly IReadOnlyList<string> All =
        [Complete, AwaitingSap, NotFiscalised, InProgress, NeedsAttention];

    public static string Decide(bool fiscalised, bool inSap, bool hasFailure) =>
        fiscalised && inSap ? Complete
        : hasFailure ? NeedsAttention
        : fiscalised ? AwaitingSap
        : inSap ? NotFiscalised
        : InProgress;

    public static bool IsKnown(string? state) =>
        state is not null && All.Contains(state, StringComparer.Ordinal);
}
