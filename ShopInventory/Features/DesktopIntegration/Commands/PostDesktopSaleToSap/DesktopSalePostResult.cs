namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;

/// <summary>
/// What posting one sale did, and the document it left in SAP.
/// </summary>
/// <remarks>
/// <see cref="DesktopSalePostOutcomes.AlreadyInSap"/> is kept apart from
/// <see cref="DesktopSalePostOutcomes.Posted"/> rather than folded into a boolean. They differ in the
/// only way that matters here: one created a document and one found one that was already there, and
/// an operator who presses Post twice needs to be told which of those just happened.
/// </remarks>
public sealed record DesktopSalePostResult(
    string ExternalReferenceId,
    string Outcome,
    int? SapDocEntry,
    int? SapDocNum,
    string? Message
);

/// <summary>The values <see cref="DesktopSalePostResult.Outcome"/> takes.</summary>
public static class DesktopSalePostOutcomes
{
    /// <summary>A new SAP invoice was created for this sale.</summary>
    public const string Posted = "Posted";

    /// <summary>SAP already held the invoice, so it was adopted rather than raised again.</summary>
    public const string AlreadyInSap = "AlreadyInSap";

    /// <summary>Nothing was sent: another post for this sale holds the claim.</summary>
    public const string InProgress = "InProgress";

    /// <summary>SAP refused the document, or could not be asked whether it already held one.</summary>
    public const string Failed = "Failed";

    /// <summary>The sale is not one that may be posted as an invoice of its own.</summary>
    public const string NotPostable = "NotPostable";
}
