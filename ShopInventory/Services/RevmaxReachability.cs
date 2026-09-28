namespace ShopInventory.Services;

/// <summary>
/// Remembers that the REVMax device stopped answering, so reads fail at once for a short while
/// instead of each waiting out the full timeout.
/// </summary>
/// <remarks>
/// The client waits 90 seconds for the device. While it was hung, every lookup waited that long and
/// the fiscalisation sweep, the invoice posting job and the status backfill each queued more of them,
/// so a single outage held job threads for hours. After a read ends in a timeout or a connection
/// failure, reads now fail with <see cref="RevmaxUnreachableException"/> for <see cref="Cooldown"/>,
/// without being sent; any answer from the device, even an error status, ends that at once.
///
/// Receipt submissions (TransactM) are never refused here. Every submission is preceded by a lookup,
/// so while the device is down none is reached, and refusing one would read to the caller as an
/// attempt whose outcome is unknown.
///
/// A singleton: the typed client is created per use, and the whole point is that the next caller
/// knows what the last one found.
/// </remarks>
public sealed class RevmaxReachability(TimeProvider? timeProvider = null)
{
    /// <summary>How long reads are refused after the device failed to answer.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _refuseUntilTicks;

    /// <summary>
    /// The time until which reads are refused, or null when the device should be asked.
    /// </summary>
    public DateTimeOffset? RefusingUntil
    {
        get
        {
            var until = Interlocked.Read(ref _refuseUntilTicks);
            return until > _timeProvider.GetUtcNow().UtcTicks
                ? new DateTimeOffset(until, TimeSpan.Zero)
                : null;
        }
    }

    /// <summary>Records that a request got no answer: it timed out or could not connect.</summary>
    public void MarkUnreachable() =>
        Interlocked.Exchange(ref _refuseUntilTicks, (_timeProvider.GetUtcNow() + Cooldown).UtcTicks);

    /// <summary>Records that the device answered, whatever it said.</summary>
    public void MarkAnswered() => Interlocked.Exchange(ref _refuseUntilTicks, 0);

    /// <summary>
    /// True when <paramref name="exception"/>, or one it wraps, says the device did not answer at all:
    /// a refused read, a connection failure or a timeout, as opposed to an answer the caller disliked.
    /// </summary>
    public static bool IsNoAnswer(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is RevmaxUnreachableException or HttpRequestException { StatusCode: null } or TimeoutException)
                return true;

            if (current is TaskCanceledException { InnerException: TimeoutException })
                return true;
        }

        return false;
    }
}

/// <summary>
/// A read that was not sent, because the REVMax device failed to answer moments ago.
/// </summary>
/// <remarks>
/// An <see cref="HttpRequestException"/>, so every caller that already handles the device being
/// unreachable handles this the same way.
/// </remarks>
public sealed class RevmaxUnreachableException(DateTimeOffset until)
    : HttpRequestException(
        $"REVMax did not answer a request moments ago, so it is not being asked again until {until:HH:mm:ss} UTC.")
{
    public DateTimeOffset Until { get; } = until;
}
