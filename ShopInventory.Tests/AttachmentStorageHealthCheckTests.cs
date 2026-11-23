using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ShopInventory.Health;

namespace ShopInventory.Tests;

/// <summary>
/// On 2026-11-23 every POD upload from 06:25 failed with "The network path was not found", because a
/// link under the upload root led to a share that had dropped. No health check covered the store, so
/// the alert mail never went out. These tests build that same failure, a link to a share that does not
/// exist, and check that the health check reports it.
/// </summary>
public sealed class AttachmentStorageHealthCheckTests : IDisposable
{
    private readonly string _uploadRoot = Path.Combine(Path.GetTempPath(), $"attachment-probe-{Guid.NewGuid():N}");
    private readonly CapturingLogger<AttachmentStorageMonitor> _logger = new();
    private readonly AttachmentStorageMonitor _monitor;

    public AttachmentStorageHealthCheckTests()
    {
        Directory.CreateDirectory(_uploadRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:UploadPath"] = _uploadRoot })
            .Build();
        _monitor = new AttachmentStorageMonitor(configuration, TimeProvider.System, _logger);
    }

    private string Attachments => Path.Combine(_uploadRoot, "attachments");

    [Fact]
    public async Task A_reachable_store_is_healthy_and_keeps_no_probe_file()
    {
        Directory.CreateDirectory(Path.Combine(Attachments, "Invoice", "2431681"));
        Directory.CreateDirectory(Path.Combine(Attachments, "CratePodSubmission"));

        var result = await _monitor.ProbeAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(3, result.FoldersChecked); // attachments, Invoice, CratePodSubmission
        Assert.Equal(HealthStatus.Healthy, Evaluate().Status);
        Assert.Empty(Directory.GetFiles(_uploadRoot, "*", SearchOption.AllDirectories));
        Assert.Empty(_logger.AtOrAbove(LogLevel.Warning));
    }

    [Fact]
    public async Task A_store_with_no_uploads_yet_gets_its_attachments_folder()
    {
        var result = await _monitor.ProbeAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(1, result.FoldersChecked);
        Assert.True(Directory.Exists(Attachments));
    }

    [Fact]
    public async Task A_dead_link_for_one_entity_type_is_found_and_named()
    {
        Directory.CreateDirectory(Attachments);
        CreateDeadLink(Path.Combine(Attachments, "Invoice"));

        var first = await _monitor.ProbeAsync(CancellationToken.None);

        Assert.False(first.Stored);
        Assert.Equal(Path.Combine("attachments", "Invoice"), first.FailedFolder);
        Assert.Equal(AttachmentStorageProbeStage.Write, first.FailedStage);

        // One failure could be a blip, so it is Degraded and the alert job waits for it to hold.
        var afterOne = Evaluate();
        Assert.Equal(HealthStatus.Degraded, afterOne.Status);

        await _monitor.ProbeAsync(CancellationToken.None);
        var afterTwo = Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, afterTwo.Status);
        Assert.Contains(Path.Combine("attachments", "Invoice"), afterTwo.Description);
        Assert.Contains("Every POD and crate POD upload fails", afterTwo.Description);
        Assert.Equal(2, afterTwo.Data["consecutiveFailures"]);
        Assert.Contains(AttachmentStorageMonitor.DescribeError(first.Error!), afterTwo.Description);

        // /api/health is anonymous: the server's folder layout stays in the log.
        Assert.DoesNotContain(_uploadRoot, afterTwo.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            afterTwo.Data.Values.OfType<string>(),
            value => value.Contains(_uploadRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_dead_link_at_the_attachments_folder_is_found()
    {
        CreateDeadLink(Attachments);

        var result = await _monitor.ProbeAsync(CancellationToken.None);

        Assert.False(result.Stored);
        Assert.Equal("attachments", result.FailedFolder);
    }

    [Fact]
    public async Task An_outage_logs_one_error_and_one_recovery_however_long_it_lasts()
    {
        Directory.CreateDirectory(Attachments);
        var link = Path.Combine(Attachments, "Invoice");
        CreateDeadLink(link);

        for (var probe = 0; probe < 5; probe++)
        {
            await _monitor.ProbeAsync(CancellationToken.None);
        }

        Assert.Equal(HealthStatus.Unhealthy, Evaluate().Status);
        var error = Assert.Single(_logger.AtOrAbove(LogLevel.Warning));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains(_uploadRoot, error.Message); // the full path belongs in the log

        // The share comes back.
        RemoveLink(link);
        Directory.CreateDirectory(link);
        await _monitor.ProbeAsync(CancellationToken.None);

        var health = Evaluate();
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(0, _monitor.Current.ConsecutiveFailures);
        Assert.Single(_logger.AtOrAbove(LogLevel.Warning));
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("after 5 failed probe(s)"));
    }

    [Fact]
    public void The_reason_is_the_system_text_without_the_path()
    {
        // As production threw it on 2026-11-23: ERROR_BAD_NETPATH, with the full path in the message.
        var production = new IOException(
            @"The network path was not found. : 'C:\inetpub\ShopInventory-API\uploads\attachments\Invoice\2431681'.",
            unchecked((int)0x80070035));

        var reason = AttachmentStorageMonitor.DescribeError(production);

        Assert.DoesNotContain("inetpub", reason);
        Assert.Equal(OperatingSystem.IsWindows() ? "The network path was not found." : "IOException.", reason);
        Assert.Equal("Access is denied.", AttachmentStorageMonitor.DescribeError(new UnauthorizedAccessException(@"Access to the path 'C:\x' is denied.")));
    }

    [Fact]
    public void Nothing_is_reported_before_the_first_write()
    {
        var result = AttachmentStorageHealthCheck.Evaluate(AttachmentStorageSnapshot.NotProbed, Now);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void A_write_that_has_not_come_back_counts_as_one_more_failure()
    {
        var lastGood = Succeeded(Now.AddMinutes(-1));

        var quick = AttachmentStorageHealthCheck.Evaluate(
            new AttachmentStorageSnapshot(lastGood, lastGood.FinishedAt, 0, Now.AddSeconds(-5)), Now);
        Assert.Equal(HealthStatus.Healthy, quick.Status);

        var waiting = AttachmentStorageHealthCheck.Evaluate(
            new AttachmentStorageSnapshot(lastGood, lastGood.FinishedAt, 0, Now.AddSeconds(-20)), Now);
        Assert.Equal(HealthStatus.Degraded, waiting.Status);
        Assert.Contains("has not come back", waiting.Description);

        // The write before this one failed too, so the store has now missed two in a row.
        var failed = Failed(Now.AddMinutes(-1), AttachmentStorageProbeStage.Write);
        var waitingAgain = AttachmentStorageHealthCheck.Evaluate(
            new AttachmentStorageSnapshot(failed, Now.AddMinutes(-2), 1, Now.AddSeconds(-20)), Now);
        Assert.Equal(HealthStatus.Unhealthy, waitingAgain.Status);
        Assert.Contains("The write before it failed", waitingAgain.Description);
    }

    [Fact]
    public void A_write_the_store_took_but_would_not_delete_is_only_degraded()
    {
        var result = Failed(Now.AddSeconds(-10), AttachmentStorageProbeStage.Delete);

        var health = AttachmentStorageHealthCheck.Evaluate(
            new AttachmentStorageSnapshot(result, result.FinishedAt, 0, null), Now);

        Assert.True(result.Stored);
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("Uploads still work", health.Description);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 4, 30, 0, TimeSpan.Zero);

    private HealthCheckResult Evaluate() =>
        AttachmentStorageHealthCheck.Evaluate(_monitor.Current, DateTimeOffset.UtcNow);

    private static AttachmentStorageProbeResult Succeeded(DateTimeOffset finishedAt) =>
        new(finishedAt.AddMilliseconds(-4), finishedAt, 3, @"C:\inetpub\ShopInventory-API\uploads", null, null, null);

    private static AttachmentStorageProbeResult Failed(DateTimeOffset finishedAt, AttachmentStorageProbeStage stage) =>
        new(finishedAt.AddSeconds(-21), finishedAt, 1, @"C:\inetpub\ShopInventory-API\uploads",
            Path.Combine("attachments", "Invoice"), stage, new IOException("The network path was not found."));

    /// <summary>
    /// A folder link to a folder that does not exist. A link to a share on a missing host fails the
    /// same way, with "The network path was not found" (checked by hand on Windows, 2026-11-23). But
    /// Windows takes up to 15 seconds to give up on a host, and the error it returns varies from run
    /// to run, so the tests use a local target instead.
    /// </summary>
    private static void CreateDeadLink(string linkPath) =>
        Directory.CreateSymbolicLink(
            linkPath,
            Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}", "attachments"));

    /// <summary>
    /// Removes the link itself, never what it points at. Windows made it as a directory link, which
    /// only <see cref="Directory.Delete(string)"/> removes. Linux sees a dead link as a file, so
    /// <c>Directory.Delete</c> there throws DirectoryNotFoundException (as CI found) and
    /// <see cref="File.Delete"/> unlinks it.
    /// </summary>
    private static void RemoveLink(string linkPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(linkPath);
        }
        else
        {
            File.Delete(linkPath);
        }
    }

    public void Dispose()
    {
        try
        {
            // Links are removed, not followed, so a dead one does not stop the clean-up.
            Directory.Delete(_uploadRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
