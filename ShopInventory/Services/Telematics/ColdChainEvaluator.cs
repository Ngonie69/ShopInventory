namespace ShopInventory.Services.Telematics;

/// <summary>One probe reading, reduced to what the cold-chain arithmetic needs.</summary>
public readonly record struct TemperaturePoint(DateTime AtUtc, decimal Celsius);

/// <summary>What one probe said over one day, judged against the limits the round carried.</summary>
/// <remarks>
/// The two minute counts are null when there were no limits to judge against, and zero when there
/// were and nothing breached them. Those are different findings, and the report says so.
/// </remarks>
public sealed record ColdChainSummary(
    int SampleCount,
    decimal? MinC,
    decimal? MaxC,
    decimal? AvgC,
    DateTime? FirstSampleUtc,
    DateTime? LastSampleUtc,
    int? MinutesAboveMax,
    int? MinutesBelowMin);

/// <summary>
/// Turns a day of probe readings into the figures a cold-chain check reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a breach is timed.</b> A reading is taken to hold until the next one, so each interval
/// between consecutive readings counts towards whichever side of the limits its opening reading
/// sat on. The last reading of the day opens no interval and counts nothing.
/// </para>
/// <para>
/// <b>A gap is not a breach.</b> Each interval is capped at <c>gapCap</c>. A probe that reported
/// 12 °C and then fell silent for four hours has shown one warm reading, not a four-hour breach:
/// nothing was measured in between. Without the cap a van out of coverage would turn one warm
/// reading into a long breach, and that is an accusation the data does not support.
/// </para>
/// </remarks>
public static class ColdChainEvaluator
{
    public static ColdChainSummary Evaluate(
        IReadOnlyCollection<TemperaturePoint> readings,
        decimal? limitMinC,
        decimal? limitMaxC,
        TimeSpan gapCap)
    {
        var series = readings.OrderBy(point => point.AtUtc).ToList();
        var judged = limitMinC is not null || limitMaxC is not null;

        if (series.Count == 0)
        {
            return new ColdChainSummary(0, null, null, null, null, null,
                judged ? 0 : null,
                judged ? 0 : null);
        }

        double secondsAbove = 0;
        double secondsBelow = 0;

        for (var i = 0; i < series.Count - 1; i++)
        {
            var span = series[i + 1].AtUtc - series[i].AtUtc;

            if (span > gapCap)
            {
                span = gapCap;
            }

            var reading = series[i].Celsius;

            if (limitMaxC is { } max && reading > max)
            {
                secondsAbove += span.TotalSeconds;
            }
            else if (limitMinC is { } min && reading < min)
            {
                secondsBelow += span.TotalSeconds;
            }
        }

        return new ColdChainSummary(
            series.Count,
            series.Min(point => point.Celsius),
            series.Max(point => point.Celsius),
            Math.Round(series.Average(point => point.Celsius), 2),
            series[0].AtUtc,
            series[^1].AtUtc,
            judged ? (int)Math.Round(secondsAbove / 60) : null,
            judged ? (int)Math.Round(secondsBelow / 60) : null);
    }
}
