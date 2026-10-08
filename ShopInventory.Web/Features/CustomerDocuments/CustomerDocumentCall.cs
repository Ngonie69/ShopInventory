using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CustomerDocuments;

/// <summary>
/// The one way the customer-document handlers turn an API call into a result for the screen.
/// </summary>
/// <remarks>
/// The rules — consent, opt-outs, which invoices may be sent, the caps — all live on the API and are
/// enforced there. The handlers do not re-check them: a second copy would drift, and the one that
/// matters is the one the API applies. They carry the API's sentence back, and log what it said.
/// </remarks>
internal static class CustomerDocumentCall
{
    public static async Task<ErrorOr<T>> RunAsync<T>(
        Func<Task<T>> call,
        string fallback,
        ILogger logger,
        string operation)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Customer documents: {Operation} failed", operation);
            return Errors.CustomerDocuments.Failed(ApiErrorResponse.GetFriendlyMessage(ex, fallback));
        }
    }
}
