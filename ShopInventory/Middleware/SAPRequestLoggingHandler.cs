using System.Diagnostics;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Middleware;

/// <summary>
/// DelegatingHandler that records actual SAP Service Layer requests for the sync history UI.
/// </summary>
/// <remarks>
/// Recording is a hand-off to <see cref="SapRequestLogQueue"/> and nothing more. This handler is
/// registered innermost, so anything it does runs while the caller still holds a SAP concurrency
/// slot and a pooled connection; the database work therefore belongs to
/// <see cref="SapRequestLogWriter"/>, not here.
/// </remarks>
public class SAPRequestLoggingHandler(SapRequestLogQueue logQueue) : DelegatingHandler
{
    private const int MaxEndpointLength = 200;
    private const int MaxErrorLength = 1000;
    private const string ServiceLayerPrefix = "/b1s/v1/";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        Exception? failure = null;

        try
        {
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            Record(request, response, failure, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void Record(
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception? failure,
        double responseTimeMs)
    {
        var endpoint = FormatEndpoint(request);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        logQueue.TryEnqueue(new SapConnectionLog
        {
            IsSuccess = failure is null && response?.IsSuccessStatusCode == true,
            ResponseTimeMs = responseTimeMs,
            ErrorMessage = Truncate(FormatError(response, failure), MaxErrorLength),
            Endpoint = Truncate(endpoint, MaxEndpointLength),
            CheckedAt = DateTime.UtcNow
        });
    }

    private static string FormatEndpoint(HttpRequestMessage request)
    {
        var requestTarget = request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.PathAndQuery
            : request.RequestUri?.OriginalString;

        if (string.IsNullOrWhiteSpace(requestTarget))
        {
            return string.Empty;
        }

        var path = requestTarget;
        var queryIndex = path.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        if (path.StartsWith(ServiceLayerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[ServiceLayerPrefix.Length..];
        }

        path = path.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "Root";
        }

        return $"{request.Method.Method.ToUpperInvariant()} {path}";
    }

    private static string? FormatError(HttpResponseMessage? response, Exception? failure)
    {
        if (failure is not null)
        {
            return failure.Message;
        }

        if (response is null || response.IsSuccessStatusCode)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? $"{(int)response.StatusCode} {response.StatusCode}"
            : $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
