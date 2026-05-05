using System.Net;

namespace ShopInventory.Services;

public static class SapFailureClassifier
{
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is SapCircuitOpenException ||
            exception is HttpRequestException ||
            exception is TimeoutException ||
            exception is TaskCanceledException)
        {
            return true;
        }

        if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
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