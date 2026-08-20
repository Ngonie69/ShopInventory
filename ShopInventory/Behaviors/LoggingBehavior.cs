using System.Diagnostics;
using ErrorOr;
using MediatR;

namespace ShopInventory.Behaviors;

/// <summary>
/// Records the outcome of every MediatR request: what it was, how long it took, and how it ended.
/// </summary>
/// <remarks>
/// This used to write two lines per request — <c>Handling X</c> then <c>Handled X</c> — carrying
/// nothing but the name. In a nine-hour production log that was 3,206 lines, 38% of the file, and
/// it said nothing an operator could act on: a handler that succeeded and a handler that returned
/// an error to the user looked identical, and the time between the pair was the only clue to how
/// long anything took.
/// <para>
/// The cost was not the volume. A paged stock request spent exactly 60.000 seconds, failed, and
/// left only its <c>Handling</c>/<c>Handled</c> pair behind, because the catch that produced the
/// error had no logger call in it. There are 58 catch blocks in this codebase shaped that way
/// (<c>scripts/find_silent_catches.py</c>) and hand-writing a log line into each is both a lot of
/// churn and no guarantee about the 59th. Reporting the outcome here covers all of them at once,
/// including every one written after this.
/// </para>
/// <para>
/// Levels are chosen so the level means something. A fast success is <c>Debug</c> — it is the
/// normal case and nobody needs a line for it. A slow one is <c>Information</c>, because duration
/// is the thing worth noticing. A returned error is <c>Information</c>: an
/// <see cref="IErrorOr"/> failure is the handler answering the question, usually a validation or
/// business refusal, and promoting those to Warning would repeat the mistake that made every
/// error-level line in that log a salesperson hitting a credit limit. An escaping exception is
/// <c>Warning</c> — the handler did not answer at all — except a client disconnect, which is
/// <c>Information</c> because the caller going away is not a fault.
/// </para>
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Past this, the duration is the story and the line is worth an operator's attention. Chosen
    /// from the production distribution: almost every handler answers inside 500 ms, while the ones
    /// that hurt — order approval at 3.6–25.6s, the POD status report at 20–64s — are far above it.
    /// </summary>
    private static readonly TimeSpan SlowRequestThreshold = TimeSpan.FromSeconds(1);

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        // Kept at Debug rather than dropped: when a request hangs there is no completion line, and
        // this is the only evidence it ever started. That is a debugging session, not a normal day.
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
