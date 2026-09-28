namespace ShopInventory.Web.Services;

/// <summary>
/// Cancels a GET when the page that made it has been left; see <see cref="PageReads"/>.
/// </summary>
/// <remarks>
/// Reads only. A save cut off part-way can leave a document posted with nobody told, so anything
/// but GET and HEAD passes untouched, as does a background sweep: those are shared, and marked by
/// <see cref="SapBackgroundPriority"/>. The API binds each action's token to the request, so the
/// abort reaches its SAP call rather than stopping at the Web.
/// </remarks>
public sealed class PageReadCancellationHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var pageToken = PageReads.Token;
        if (!pageToken.CanBeCanceled ||
            SapBackgroundPriority.IsBackground ||
            (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pageToken);
        return await base.SendAsync(request, linked.Token);
    }
}
