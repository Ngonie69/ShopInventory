namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// Tells the administrators when sending customer documents needs them — at most once per condition
/// per cooldown, never once per document.
/// </summary>
public interface ICustomerDocumentAlerts
{
    /// <summary>Raises the alert unless the same condition was raised within the cooldown. Never throws.</summary>
    Task RaiseAsync(string condition, string title, string message, CancellationToken cancellationToken);
}
