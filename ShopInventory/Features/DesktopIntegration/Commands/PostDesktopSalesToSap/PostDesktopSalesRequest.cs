namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSalesToSap;

/// <summary>
/// The body of a bulk post: the sales to send, by their own external references.
/// </summary>
/// <remarks>
/// A wrapper object rather than a bare array so the request can grow a second field — a reason, a
/// posting date — without every existing caller's body becoming invalid.
/// </remarks>
public sealed class PostDesktopSalesRequest
{
    public List<string>? ExternalReferenceIds { get; set; }

    /// <summary>
    /// Admin only: post sales that have no fiscal receipt yet, during a fiscal outage.
    /// </summary>
    public bool WithoutReceipt { get; set; }
}
