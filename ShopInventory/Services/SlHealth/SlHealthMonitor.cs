using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.SlHealth;

/// <summary>
/// What SL Health last said about the SAP Service Layer, as this node heard it.
/// </summary>
/// <remarks>
/// <para>
/// SL Health is a second opinion. It can explain an outage (planned maintenance, a restart, failing
/// logins) and it sees the queries a ping does not, but it can also be wrong about the Service Layer: a
/// firewall between the two is enough. So nothing here is ever the only reason to refuse work, and
/// every way of not having an answer is the same as SL Health not existing. <see cref="Current"/> is
/// null when the reader is off, SL Health has not answered, the last answer is older than
/// <see cref="MaxAge"/>, SL Health marked it <c>stale</c>, or its advice is <c>Unknown</c>.
/// </para>
/// <para>
/// Each node asks for itself every <see cref="RefreshInterval"/> through
/// <see cref="SlHealthRefresher"/> and keeps the last answer in memory. Reading it never leaves the
/// process. An answer's age is counted from when this node received it. SL Health's own clock is not
/// compared with this one: its <c>stale</c> flag already says when its checks have stopped.
/// </para>
/// </remarks>
public sealed class SlHealthMonitor(
    IServiceScopeFactory scopeFactory,
    IOptions<SlHealthSettings> settings,
    TimeProvider timeProvider,
    ILogger<SlHealthMonitor> logger)
{
    /// <summary>How often each node asks SL Health.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>An answer received longer ago than this is ignored.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long SL Health must keep reporting a failing login or query before staff are told SAP is
    /// answering slowly. Long enough that one bad sample, or a query that is slow once, says nothing.
    /// </summary>
    public static readonly TimeSpan TroubleNoticeAfter = TimeSpan.FromMinutes(2);

    private volatile Reading _reading = Reading.None;
    private string? _lastFailure;

    /// <summary>Whether SL Health is asked at all.</summary>
    public bool IsConfigured => settings.Value.IsConfigured;

    /// <summary>The last answer, when there is one fit to act on. Never leaves the process.</summary>
    public SlHealthStatus? Current => Usable(_reading, Now);

    /// <summary>
    /// Since when SL Health has reported a failing login, query or Server Tools behind a ping that
    /// answers, without a break, as this node heard it. Null when it reports none, or when there is no
    /// answer fit to act on.
    /// </summary>
    public DateTime? TroubleSinceUtc
    {
        get
        {
            var reading = _reading;
            return Usable(reading, Now) is null ? null : reading.TroubleSinceUtc;
        }
    }

    /// <summary>
    /// <see cref="TroubleSinceUtc"/> once the run has lasted <see cref="TroubleNoticeAfter"/>, which is
    /// when staff are told SAP is answering slowly. Null before then.
    /// </summary>
    public DateTime? SlowNoticeSinceUtc =>
        TroubleSinceUtc is { } since && Now - since >= TroubleNoticeAfter ? since : null;

    /// <summary>Why SAP is unavailable or what about it is failing, for the staff banner. See <see cref="SlHealthExplanation.ForStaff"/>.</summary>
    public string? ExplanationForStaff => SlHealthExplanation.ForStaff(Current, Now);

    /// <summary>The same for the outage row and the health check. See <see cref="SlHealthExplanation.ForRecord"/>.</summary>
    public string? ExplanationForRecord => SlHealthExplanation.ForRecord(Current, Now);

    /// <summary>Asks SL Health once and keeps the answer. A failed ask keeps the last answer, which ages out.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        SlHealthAnswer answer;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<ISlHealthClient>();
            answer = await client.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            answer = SlHealthAnswer.Failed($"SL Health could not be asked ({ex.GetType().Name}).");
        }

        var now = Now;
        var previous = _reading;
        var next = answer.Status is { } status
            ? new Reading(status, now, null)
            : previous;

        // A run of trouble is only as long as the answers that vouch for it. One that cannot be acted on
        // ends the run, so the two minutes are never made up of answers nobody would otherwise trust.
        var usable = Usable(next, now);
        next = next with
        {
            TroubleSinceUtc = usable is { HasFailingCheckBehindPing: true }
                ? Usable(previous, now) is not null ? previous.TroubleSinceUtc ?? now : now
                : null
        };

        _reading = next;
        Report(answer, usable);
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private static SlHealthStatus? Usable(Reading reading, DateTime now) =>
        reading.Status is { Stale: false } status
        && status.AdviceValue != SlHealthAdvice.Unknown
        && now - reading.ReceivedAtUtc <= MaxAge
        && now >= reading.ReceivedAtUtc
            ? status
            : null;

    /// <summary>One line when the reader stops having an answer and one when it has one again, not one every 15 seconds.</summary>
    private void Report(SlHealthAnswer answer, SlHealthStatus? usable)
    {
        var failure = answer.Failure
            ?? (usable is null ? "SL Health's answer is stale or it has no advice yet." : null);

        if (failure == _lastFailure)
        {
            return;
        }

        if (failure is null)
        {
            logger.LogInformation("SL Health is being read: {Level}, advice {Advice}.", usable!.Level, usable.AdviceValue);
        }
        else
        {
            logger.LogWarning("SL Health is not being read, so SAP is judged without it: {Failure}", failure);
        }

        _lastFailure = failure;
    }

    /// <param name="Status">The last answer SL Health gave, usable or not.</param>
    /// <param name="ReceivedAtUtc">When this node received <paramref name="Status"/>, by its own clock.</param>
    /// <param name="TroubleSinceUtc">When the unbroken run of answers with a failing check began.</param>
    private sealed record Reading(SlHealthStatus? Status, DateTime ReceivedAtUtc, DateTime? TroubleSinceUtc)
    {
        public static readonly Reading None = new(null, default, null);
    }
}
