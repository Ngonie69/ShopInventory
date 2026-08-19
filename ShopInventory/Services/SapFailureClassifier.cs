using System.Net;

namespace ShopInventory.Services;

public static class SapFailureClassifier
{
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is SapCircuitOpenException ||
            exception is HttpRequestException ||
            exception is TimeoutException)
        {
            return true;
        }

        return ContainsAvailabilitySignal(exception.GetBaseException().Message);
    }

    public static bool ContainsAvailabilitySignal(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        return normalized.Contains("circuit") ||
               normalized.Contains("timeout") ||
               normalized.Contains("timed out") ||
               normalized.Contains("connection") ||
               normalized.Contains("network") ||
               normalized.Contains("unavailable") ||
               normalized.Contains("service unavailable") ||
               normalized.Contains("temporarily") ||
               normalized.Contains("refused") ||
               normalized.Contains("name or service") ||
               normalized.Contains("502") ||
               normalized.Contains("503") ||
               normalized.Contains("504");
    }

    /// <summary>
    /// A rejection that says the stock is not there. Retrying cannot clear it — the document has
    /// to be re-cut or the warehouse reconciled — so the work belongs in front of a person rather
    /// than back on the queue.
    /// </summary>
    public static bool IsPermanentStockRejection(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        return (normalized.Contains("insufficient") && (normalized.Contains("stock") || normalized.Contains("quantity")))
               || normalized.Contains("not enough")
               || normalized.Contains("negative inventory")
               || normalized.Contains("quantity falls");
    }

    public static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout ||
               (int)statusCode >= 500;
    }
}