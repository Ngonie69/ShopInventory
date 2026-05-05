using ShopInventory.Services;

namespace ShopInventory.Middleware;

public sealed class SAPCircuitBreakerHandler(
    SapCircuitBreakerState circuitBreakerState,
    ILogger<SAPCircuitBreakerHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (circuitBreakerState.ShouldShortCircuit(out var retryAfter))
        {
            throw new SapCircuitOpenException(
                $"SAP circuit breaker is open. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds.");
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (SapFailureClassifier.IsTransientStatusCode(response.StatusCode))
            {
                circuitBreakerState.RecordFailure($"{(int)response.StatusCode} {response.ReasonPhrase}".Trim());
                if (circuitBreakerState.IsOpen)
                {
                    logger.LogWarning("SAP circuit breaker opened after response {StatusCode}", response.StatusCode);
                }
            }
            else
            {
                circuitBreakerState.RecordSuccess();
            }

            return response;
        }
        catch (Exception ex) when (SapFailureClassifier.IsTransient(ex, cancellationToken))
        {
            circuitBreakerState.RecordFailure(ex.Message);
            if (circuitBreakerState.IsOpen)
            {
                logger.LogWarning(ex, "SAP circuit breaker opened after transient failure");
            }

            throw;
        }
    }
}