using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ShopInventory.Services;

/// <summary>
/// Signs this process in to a Windows file share under an explicit account, and keeps that sign-in for
/// as long as anybody in the process is still using it.
/// </summary>
/// <remarks>
/// <para>
/// Windows does not count these connections. Two <c>WNetAddConnection2</c> calls for the same share in
/// one logon session both answer success and leave a single entry, and the first
/// <c>WNetCancelConnection2</c> removes it for both. Measured on <c>\\kfldb\b1_shf</c> on 2026-11-05:
/// connect, connect, cancel left no entry, and the second cancel answered 2250 (not connected).
/// </para>
/// <para>
/// Each attachment read used to connect and then force-cancel on its own. That cost nothing while the
/// share was reached under the app pool's own identity, because no credentials meant no connection. With
/// share credentials set, two people opening attachments at the same moment could pull the share out
/// from under each other. So the count lives here: the first lease connects, the last one out
/// disconnects, and a connection this process did not make is never cancelled.
/// </para>
/// <para>
/// One entry per share. The account is fixed for the life of the process, since a change to it in
/// web.config restarts the app.
/// </para>
/// </remarks>
internal sealed class NetworkShareConnections
{
    internal const int NoError = 0;
    internal const int ErrorAlreadyAssigned = 85;

    private readonly Func<string, string?, string?, int> _connect;
    private readonly Func<string, int> _disconnect;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="connect">Connects to a share root as a user with a password; answers a Win32 code.</param>
    /// <param name="disconnect">Cancels the connection to a share root; answers a Win32 code.</param>
    public NetworkShareConnections(Func<string, string?, string?, int> connect, Func<string, int> disconnect)
    {
        _connect = connect;
        _disconnect = disconnect;
    }

    /// <summary>The process-wide instance, backed by the Windows networking API.</summary>
    public static NetworkShareConnections Windows { get; } = new(ConnectWithWindows, DisconnectWithWindows);

    /// <summary>A lease for callers that need no sign-in, such as a share the app pool can already open.</summary>
    public static IDisposable None { get; } = new NoLease();

    /// <summary>How many callers hold a lease on <paramref name="shareRoot"/>. For tests.</summary>
    internal int LeaseCount(string shareRoot)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(shareRoot, out var entry) ? entry.Leases : 0;
        }
    }

    /// <summary>
    /// Signs in to <paramref name="shareRoot"/> unless this process already holds a sign-in to it, and
    /// returns a lease to dispose when the caller has finished with the share.
    /// </summary>
    /// <exception cref="Win32Exception">Windows refused the sign-in; <see cref="Win32Exception.NativeErrorCode"/> says why.</exception>
    public IDisposable Acquire(string shareRoot, string? username, string? password)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(shareRoot, out var existing))
            {
                existing.Leases++;
                return new Lease(this, shareRoot);
            }

            // Inside the lock on purpose: a second caller must wait for this sign-in rather than race it
            // to the same share, and every caller here wants the same one.
            var result = _connect(shareRoot, username, password);
            if (result != NoError && result != ErrorAlreadyAssigned)
            {
                throw new Win32Exception(result);
            }

            // Already assigned means somebody else's connection; it is used but never cancelled.
            _entries[shareRoot] = new Entry { Leases = 1, OwnedHere = result == NoError };
            return new Lease(this, shareRoot);
        }
    }

    private void Release(string shareRoot)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(shareRoot, out var entry))
            {
                return;
            }

            entry.Leases--;
            if (entry.Leases > 0)
            {
                return;
            }

            _entries.Remove(shareRoot);
            if (entry.OwnedHere)
            {
                // Not forced: every lease in this process has ended, so a file still open on the share
                // belongs to something else. If Windows keeps the connection for it, the next Acquire
                // signs in again under the same account and Windows answers success.
                _ = _disconnect(shareRoot);
            }
        }
    }

    private sealed class Entry
    {
        public int Leases { get; set; }

        public bool OwnedHere { get; init; }
    }

    private sealed class Lease(NetworkShareConnections owner, string shareRoot) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(shareRoot);
            }
        }
    }

    private sealed class NoLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    // ── Windows networking API ────────────────────────────────────────────────────────────────────

    private const int ResourceTypeDisk = 0x00000001;
    private const int ConnectTemporary = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    private static int ConnectWithWindows(string shareRoot, string? username, string? password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = shareRoot };
        return WNetAddConnection2(ref resource, password, username, ConnectTemporary);
    }

    private static int DisconnectWithWindows(string shareRoot) => WNetCancelConnection2(shareRoot, 0, force: false);
}
