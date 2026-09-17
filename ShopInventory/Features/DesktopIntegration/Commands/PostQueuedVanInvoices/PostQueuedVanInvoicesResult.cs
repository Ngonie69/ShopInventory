namespace ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;

/// <summary>
/// What one run did with the van sales it picked up.
/// </summary>
/// <param name="Posted">Now in SAP, whether this run posted the invoice or found one already there.</param>
/// <param name="Deferred">SAP could not be reached or the sale was mid-post elsewhere; tried again later.</param>
/// <param name="SentForReview">SAP refused the sale, or its reservation cannot be posted; a person has to look.</param>
public sealed record PostQueuedVanInvoicesResult(int Posted, int Deferred, int SentForReview);
