using System.Globalization;
using ShopInventory.Services;

namespace ShopInventory.Common.Mobile;

/// <summary>
/// Decides when a handset-reported event actually happened.
///
/// A van works out of coverage, so an attendance event or a day's departure is routinely recorded on
/// the handset hours before the server hears about it. Stamping <c>DateTime.UtcNow</c> on arrival —
/// which is what every one of these paths used to do — records the moment the signal came back, not
/// the moment the rep was at the shop. Every offline visit was therefore filed at the wrong time, and
/// silently: nothing in the data said the time was the sync's rather than the event's.
///
/// So the client's clock is believed, within limits, and the server separately records when it was
/// told. Two rules bound the trust:
///
/// - <b>Not the future.</b> A handset ahead of the server is clock skew, not prophecy. A couple of
///   minutes of slack absorbs ordinary drift; past that the server's own clock is the better answer.
/// - <b>Not the distant past.</b> A month is longer than any handset stays offline, so a value older
///   than that is a broken clock or an uninitialised column reading back as the year 1 — not a
///   month-old visit. Note this is a sanity bound and not a security control: it cannot tell a
///   back-dated claim from a genuine one. What does that is the recorded-at timestamp the caller
///   stores alongside, which is always the server's and always shows the gap.
/// </summary>
public static class CaptureClock
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan MaximumBacklog = TimeSpan.FromDays(30);

    /// <summary>
    /// The UTC instant to record, given what the client claimed. Falls back to <paramref name="now"/>
    /// whenever the claim is absent or outside the bounds above.
    /// </summary>
    /// <param name="claimedCat">
    /// The client's time as a CAT wall clock with no offset — the shape this app's handsets send, and
    /// the same one the offline sales upload uses for <c>sold_at</c>.
    /// </param>
    /// <param name="now">The current UTC instant, passed in so this stays testable against a fixed clock.</param>
    public static DateTime Resolve(DateTime? claimedCat, DateTime now)
    {
        if (!claimedCat.HasValue)
        {
            return now;
        }

        var claimedUtc = AuditService.FromCAT(claimedCat.Value);

        if (claimedUtc > now + FutureTolerance)
        {
            return now;
        }

        return claimedUtc < now - MaximumBacklog ? now : claimedUtc;
    }

    /// <inheritdoc cref="Resolve(DateTime?, DateTime)"/>
    public static DateTime Resolve(DateTime? claimedCat) => Resolve(claimedCat, DateTime.UtcNow);

    /// <summary>
    /// Reads the wire form the handsets send — <c>yyyy-MM-ddTHH:mm:ss</c>, or the space-separated
    /// variant, both without an offset. Returns null for anything else rather than guessing, so an
    /// unparseable value falls through to the server clock instead of landing on a wrong day.
    /// </summary>
    public static DateTime? Parse(string? capturedAt)
    {
        if (string.IsNullOrWhiteSpace(capturedAt))
        {
            return null;
        }

        // AdjustToUniversal is deliberately absent: the value carries no offset by design, and
        // treating it as UTC would shift every CAT event two hours. FromCAT does the conversion.
        return DateTime.TryParse(
            capturedAt.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }
}
