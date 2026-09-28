using System.Globalization;
using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Probes SAP on one node at a time and records outages. See <see cref="SapAvailabilityProbe"/>.
/// </summary>
/// <remarks>
/// The run of results so far is kept in the job's data map, which the clustered store persists, because
/// the next firing may land on another node.
/// </remarks>
[DisallowConcurrentExecution]
[PersistJobDataAfterExecution]
public sealed class SapAvailabilityProbeJob(IServiceScopeFactory scopeFactory) : IJob
{
    private const string FailuresKey = "failures";
    private const string SuccessesKey = "successes";
    private const string RunStartedKey = "runStartedAtUtc";
    private const string FirstErrorKey = "firstError";

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;

        await using var scope = scopeFactory.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<SapAvailabilityProbe>();

        var run = await probe.RunAsync(Read(data), context.CancellationToken);

        Write(data, run);
    }

    internal static SapProbeRun Read(JobDataMap data) => new(
        ReadInt(data, FailuresKey),
        ReadInt(data, SuccessesKey),
        data.TryGetString(RunStartedKey, out var started)
            && DateTime.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : default,
        data.TryGetString(FirstErrorKey, out var error) && !string.IsNullOrEmpty(error) ? error : null);

    internal static void Write(JobDataMap data, SapProbeRun run)
    {
        // Strings, as the other persisted job does: the store serialises the map as JSON, and a value
        // read back as a different numeric type than it was written is a KeyNotFound waiting to happen.
        data[FailuresKey] = run.Failures.ToString(CultureInfo.InvariantCulture);
        data[SuccessesKey] = run.Successes.ToString(CultureInfo.InvariantCulture);
        data[RunStartedKey] = run.RunStartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        data[FirstErrorKey] = run.FirstError ?? string.Empty;
    }

    private static int ReadInt(JobDataMap data, string key) =>
        data.TryGetString(key, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}
