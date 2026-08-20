using System.Diagnostics;
using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Behaviors;

/// <summary>
/// Records the outcome of every MediatR request: what it was, how long it took, and how it ended.
/// </summary>
/// <remarks>
/// The mirror of <c>ShopInventory.Behaviors.LoggingBehavior</c>, which carries the full rationale.
/// In short: the pair of <c>Handling X</c> / <c>Handled X</c> lines this replaces said nothing an
/// operator could act on — a success and a returned error looked identical — while accounting for
/// 38% of a production log. Reporting the outcome and the duration instead means a request that
/// fails inside a catch block with no logger call still leaves a record.
/// <para>
/// The two projects share no assembly, so this is duplicated rather than referenced, in keeping
/// with how the rest of the Web app mirrors the API. Keep the two in step.
/// </para>
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Past this, the duration is the story and the line is worth attention.</summary>
    private static readonly TimeSpan SlowRequestThreshold = TimeSpan.FromSeconds(1);

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        // Kept at Debug rather than dropped: when a request hangs there is no completion line, and
        // this is the only evidence it ever started.
        logger.LogDebug("Handling {RequestName}", requestName);

        var timer = Stopwatch.StartNew();

        TResponse response;
        try
        {
            response = await next();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            logger.LogInformation(
                "{RequestName} was canceled by the caller after {ElapsedMs} ms",
                requestName,
                timer.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            logger.LogWarning(
                ex,
                "{RequestName} failed after {ElapsedMs} ms",
                requestName,
                timer.ElapsedMilliseconds);
            throw;
        }

        timer.Stop();
        LogOutcome(requestName, response, timer.Elapsed);
        return response;
    }

    private void LogOutcome(string requestName, TResponse response, TimeSpan elapsed)
    {
        if (response is IErrorOr { IsError: true } failed)
        {
            var error = failed.Errors is { Count: > 0 } errors ? errors[0] : (Error?)null;

            logger.LogInformation(
                "{RequestName} returned {ErrorType} {ErrorCode} after {ElapsedMs} ms: {ErrorDescription}",
                requestName,
                error?.Type.ToString() ?? "Failure",
                error?.Code ?? "Unknown",
                (long)elapsed.TotalMilliseconds,
                error?.Description ?? "No description was supplied.");
            return;
        }

        if (elapsed >= SlowRequestThreshold)
        {
            logger.LogInformation(
                "{RequestName} completed in {ElapsedMs} ms",
                requestName,
                (long)elapsed.TotalMilliseconds);
            return;
        }

        logger.LogDebug(
            "{RequestName} completed in {ElapsedMs} ms",
            requestName,
            (long)elapsed.TotalMilliseconds);
    }
}
