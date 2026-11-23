using System.ComponentModel;
using ShopInventory.Services;

namespace ShopInventory.Health;

/// <summary>
/// Writes a small file into the attachment store once a minute and keeps the result, so
/// <see cref="AttachmentStorageHealthCheck"/> can answer straight away without touching the store.
/// </summary>
/// <remarks>
/// <para>
/// In production the store is a network share reached through a link under the upload root. When
/// the share drops, every POD, van-sales POD and crate POD upload fails with "The network path was
/// not found". That happened for eighty minutes on 2026-08-13, and again on 2026-11-23 from the
/// first upload of the day, 06:25, for at least 47 minutes. The second time no upload succeeded, and
/// no health check noticed, so the alert mail went out only for SAP's overnight outage.
/// </para>
/// <para>
/// The write happens here, in the background, because an unreachable share can take 20 to 60
/// seconds to fail. <c>/api/health</c> is called by the Web dashboard and by the alert job, and a
/// check that waited on the share would hang both of them. The health check reads the last result
/// instead, and treats a write that has not come back as a failure that is still happening.
/// </para>
/// <para>
/// The write goes into the attachments folder and into each folder directly below it, which are the
/// folders each entity type uploads into. Nobody has confirmed which level of the path is the link.
/// The 2026-08-13 stack trace only suggested the attachments folder, so a link at
/// <c>attachments\Invoice</c> could not be ruled out, and a write into the attachments folder alone
/// would not have caught it. Together these cover every folder an upload passes through except the
/// one it creates for its own document.
/// </para>
/// <para>
/// A change of state is logged once, so an outage costs one error and one recovery line. One line a
/// minute would only add to the noise the log already carries.
/// </para>
/// </remarks>
public sealed class AttachmentStorageMonitor(
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<AttachmentStorageMonitor> logger)
{
    /// <summary>How often the store is written to.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One name per process. If a delete fails, the next probe overwrites the same file instead of
    /// leaving a new one every minute. The blue and green slots can both be running against the one
    /// share, so the name has to differ between processes, or their writes would collide.
    /// </summary>
    public static readonly string ProbeFileName =
        $".health-probe-{Environment.MachineName}-{Environment.ProcessId}.tmp";

    private readonly object _gate = new();
    private AttachmentStorageSnapshot _snapshot = AttachmentStorageSnapshot.NotProbed;

    /// <summary>What the store last did, and whether a write is waiting on it now.</summary>
    public AttachmentStorageSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Writes into the store once and records what happened.</summary>
    public async Task<AttachmentStorageProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        lock (_gate)
        {
            _snapshot = _snapshot with { ProbeStartedAt = startedAt };
        }

        // The file calls block for as long as SMB takes to give up, and cannot be cancelled, so they
        // run off the caller's thread. On shutdown the wait is abandoned and the write is left to finish.
        var result = await Task.Run(() => Probe(startedAt), CancellationToken.None).WaitAsync(cancellationToken);

        AttachmentStorageSnapshot previous;
        AttachmentStorageSnapshot next;
        lock (_gate)
        {
            previous = _snapshot;
            next = new AttachmentStorageSnapshot(
                result,
                result.Stored ? result.FinishedAt : previous.LastStoredAt,
                result.Stored ? 0 : previous.ConsecutiveFailures + 1,
                ProbeStartedAt: null);
            _snapshot = next;
        }

        LogChange(previous, next);
        return result;
    }

    private AttachmentStorageProbeResult Probe(DateTimeOffset startedAt)
    {
        var uploadRoot = string.Empty;
        var folder = string.Empty;
        var stage = AttachmentStorageProbeStage.Create;
        var foldersChecked = 0;

        try
        {
            uploadRoot = Path.GetFullPath(DocumentStoragePath.UploadRoot(configuration));
            var attachments = DocumentStoragePath.Within(uploadRoot, DocumentStoragePath.AttachmentsFolder);
            folder = attachments;

            // An upload creates its folders the same way, so a link that has gone dead fails here
            // just as it fails for an upload.
            Directory.CreateDirectory(attachments);

            stage = AttachmentStorageProbeStage.List;
            var folders = new List<string> { attachments };
            foreach (var entry in new DirectoryInfo(attachments).EnumerateFileSystemInfos())
            {
                // A link counts whatever it points at. A dead link cannot say whether it pointed at a
                // folder, and on Linux .NET lists it as a file, so a directory-only listing would skip
                // exactly the entry this probe is looking for.
                if (entry is DirectoryInfo || entry.LinkTarget is not null)
                {
                    folders.Add(entry.FullName);
                }
            }

            foreach (var target in folders)
            {
                folder = target;
                var probeFile = Path.Combine(target, ProbeFileName);

                stage = AttachmentStorageProbeStage.Write;
                File.WriteAllBytes(probeFile, "ok"u8.ToArray());

                stage = AttachmentStorageProbeStage.Delete;
                File.Delete(probeFile);

                foldersChecked++;
            }

            return new AttachmentStorageProbeResult(
                startedAt, timeProvider.GetUtcNow(), foldersChecked, uploadRoot, null, null, null);
        }
        catch (Exception ex)
        {
            return new AttachmentStorageProbeResult(
                startedAt,
                timeProvider.GetUtcNow(),
                foldersChecked,
                uploadRoot,
                string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(uploadRoot)
                    ? folder
                    : Path.GetRelativePath(uploadRoot, folder),
                stage,
                ex);
        }
    }

    private void LogChange(AttachmentStorageSnapshot previous, AttachmentStorageSnapshot next)
    {
        var result = next.LastResult!;
        var wasFailing = previous.LastResult is { Succeeded: false };

        if (!result.Stored && !wasFailing)
        {
            logger.LogError(
                result.Error,
                "The attachment store failed a test write ({Stage} in {Folder}); POD and crate POD uploads fail until it answers again. Upload root: {UploadRoot}",
                result.FailedStage,
                result.FailedFolder,
                result.UploadRoot);
        }
        else if (!result.Succeeded && !wasFailing)
        {
            logger.LogWarning(
                result.Error,
                "The attachment store took a test write in {Folder} but would not delete it. Uploads still work. Upload root: {UploadRoot}",
                result.FailedFolder,
                result.UploadRoot);
        }
        else if (result.Succeeded && wasFailing)
        {
            logger.LogInformation(
                "The attachment store took a test write again after {Failures} failed probe(s); the last good write before them was at {LastStoredAt:O}.",
                previous.ConsecutiveFailures,
                previous.LastStoredAt);
        }
    }

    /// <summary>
    /// The reason a write failed, without the path. <c>/api/health</c> is anonymous, and .NET puts the
    /// full server path in each IO exception's message.
    /// </summary>
    public static string DescribeError(Exception error)
    {
        // On Windows every IO failure carries a Win32 code (0x8007xxxx), and the system's own text for
        // that code is the message without the path, such as "The network path was not found."
        if (OperatingSystem.IsWindows() && (error.HResult & 0xFFFF0000) == 0x80070000)
        {
            return new Win32Exception(error.HResult & 0xFFFF).Message.TrimEnd('.') + ".";
        }

        return error is UnauthorizedAccessException
            ? "Access is denied."
            : $"{error.GetType().Name}.";
    }
}

/// <summary>The step of a test write that failed.</summary>
public enum AttachmentStorageProbeStage
{
    /// <summary>Creating the attachments folder.</summary>
    Create,

    /// <summary>Listing the folders under it.</summary>
    List,

    /// <summary>Writing the test file.</summary>
    Write,

    /// <summary>Deleting the test file. The store took the write, so uploads still work.</summary>
    Delete
}

/// <summary>One test write into the attachment store.</summary>
/// <param name="StartedAt">When the write started.</param>
/// <param name="FinishedAt">When it succeeded or failed.</param>
/// <param name="FoldersChecked">Folders written to and cleaned up before it finished or failed.</param>
/// <param name="UploadRoot">The full upload root. For the server log only, never the health output.</param>
/// <param name="FailedFolder">The folder that failed, relative to the upload root.</param>
/// <param name="FailedStage">The step that failed.</param>
/// <param name="Error">Why it failed, or null if it worked.</param>
public sealed record AttachmentStorageProbeResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int FoldersChecked,
    string UploadRoot,
    string? FailedFolder,
    AttachmentStorageProbeStage? FailedStage,
    Exception? Error)
{
    public bool Succeeded => Error is null;

    /// <summary>Whether an upload would have been stored: true unless the failure came before the file was written.</summary>
    public bool Stored => Error is null || FailedStage == AttachmentStorageProbeStage.Delete;

    public TimeSpan Duration => FinishedAt - StartedAt;
}

/// <summary>What the monitor knows about the store at one moment.</summary>
/// <param name="LastResult">The last write that finished, or null before the first one.</param>
/// <param name="LastStoredAt">When a write last reached the store.</param>
/// <param name="ConsecutiveFailures">Finished writes in a row that the store did not take.</param>
/// <param name="ProbeStartedAt">When the write in progress started, or null if none is running.</param>
public sealed record AttachmentStorageSnapshot(
    AttachmentStorageProbeResult? LastResult,
    DateTimeOffset? LastStoredAt,
    int ConsecutiveFailures,
    DateTimeOffset? ProbeStartedAt)
{
    public static readonly AttachmentStorageSnapshot NotProbed = new(null, null, 0, null);
}

/// <summary>Writes into the attachment store every <see cref="AttachmentStorageMonitor.ProbeInterval"/>.</summary>
public sealed class AttachmentStorageProbeService(
    AttachmentStorageMonitor monitor,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start-up runs this method up to its first await, so yield before the first write. A dead
        // share must not hold up the API starting.
        await Task.Yield();

        using var timer = new PeriodicTimer(AttachmentStorageMonitor.ProbeInterval, timeProvider);
        try
        {
            do
            {
                await monitor.ProbeAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
