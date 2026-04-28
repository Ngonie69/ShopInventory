using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Middleware;

/// <summary>
/// DelegatingHandler that records actual SAP Service Layer requests for the sync history UI.
/// </summary>
public class SAPRequestLoggingHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<SAPRequestLoggingHandler> logger
) : DelegatingHandler
{
    private const int MaxLogEntries = 100;
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
            await TryPersistLogAsync(request, response, failure, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task TryPersistLogAsync(
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

        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            context.SapConnectionLogs.Add(new SapConnectionLog
            {
                IsSuccess = failure is null && response?.IsSuccessStatusCode == true,
                ResponseTimeMs = responseTimeMs,
                ErrorMessage = Truncate(FormatError(response, failure), MaxErrorLength),
                Endpoint = Truncate(endpoint, MaxEndpointLength),
                CheckedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var oldLogIds = await context.SapConnectionLogs
                .OrderByDescending(log => log.CheckedAt)
                .Skip(MaxLogEntries)
                .Select(log => log.Id)
                .ToListAsync();

            if (oldLogIds.Count > 0)
            {
                await context.SapConnectionLogs
                    .Where(log => oldLogIds.Contains(log.Id))
                    .ExecuteDeleteAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to persist SAP request log for {Endpoint}", endpoint);
        }
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