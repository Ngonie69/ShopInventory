namespace ShopInventory.Web.Services;

/// <summary>
/// Adds <see cref="SapBackgroundPriority.HeaderName"/> to requests made inside a
/// <see cref="SapBackgroundPriority"/> scope, and leaves every other request untouched.
/// </summary>
public sealed class SapBackgroundPriorityHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (SapBackgroundPriority.IsBackground && !request.Headers.Contains(SapBackgroundPriority.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(SapBackgroundPriority.HeaderName, SapBackgroundPriority.BackgroundValue);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
