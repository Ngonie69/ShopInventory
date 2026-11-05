using System.ComponentModel;
using System.Diagnostics;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The SAP attachments share sign-in is shared by every reader in the process and dropped by the last.
/// </summary>
/// <remarks>
/// Windows keeps one connection entry per share however many times it is connected, and one cancel
/// removes it (measured on <c>\\kfldb\b1_shf</c>, 2026-11-05). Each read used to connect and
/// force-cancel by itself, so with share credentials set, two overlapping attachment reads could
/// disconnect each other.
/// </remarks>
public class NetworkShareConnectionsTests
{
    private const string Share = @"\\files\b1_shf";

    [Fact]
    public void Overlapping_readers_sign_in_once_and_only_the_last_one_out_disconnects()
    {
        var windows = new FakeWindows();
        var connections = windows.Connections();

        var first = connections.Acquire(Share, @"KFL\share-user", "secret");
        var second = connections.Acquire(Share, @"KFL\share-user", "secret");

        Assert.Equal(1, windows.Connects);
        Assert.Equal(2, connections.LeaseCount(Share));

        first.Dispose();
        Assert.Equal(0, windows.Disconnects);

        second.Dispose();
        Assert.Equal(1, windows.Disconnects);
        Assert.Equal(0, connections.LeaseCount(Share));

        // A later reader signs in afresh.
        using var third = connections.Acquire(Share, @"KFL\share-user", "secret");
        Assert.Equal(2, windows.Connects);
    }

    [Fact]
    public void A_lease_disposed_twice_counts_once()
    {
        var windows = new FakeWindows();
        var connections = windows.Connections();

        var first = connections.Acquire(Share, "user", "secret");
        using var second = connections.Acquire(Share, "user", "secret");

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, connections.LeaseCount(Share));
        Assert.Equal(0, windows.Disconnects);
    }

    [Fact]
    public void A_refused_sign_in_holds_nothing_and_the_next_reader_tries_again()
    {
        var windows = new FakeWindows { NextResult = 1326 };
        var connections = windows.Connections();

        var refused = Assert.Throws<Win32Exception>(() => connections.Acquire(Share, "user", "wrong"));

        Assert.Equal(1326, refused.NativeErrorCode);
        Assert.Equal(0, connections.LeaseCount(Share));

        windows.NextResult = NetworkShareConnections.NoError;
        using var lease = connections.Acquire(Share, "user", "right");
        Assert.Equal(2, windows.Connects);
        Assert.Equal(1, connections.LeaseCount(Share));
    }

    [Fact]
    public void A_connection_somebody_else_made_is_used_but_never_cancelled()
    {
        var windows = new FakeWindows { NextResult = NetworkShareConnections.ErrorAlreadyAssigned };
        var connections = windows.Connections();

        connections.Acquire(Share, "user", "secret").Dispose();

        Assert.Equal(1, windows.Connects);
        Assert.Equal(0, windows.Disconnects);
    }

    [Fact]
    public async Task Readers_racing_for_the_share_sign_in_once_and_disconnect_once()
    {
        var windows = new FakeWindows();
        var connections = windows.Connections();
        using var holder = connections.Acquire(Share, "user", "secret");

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            using var lease = connections.Acquire(Share, "user", "secret");
            Thread.SpinWait(1000);
        })));

        Assert.Equal(1, windows.Connects);
        Assert.Equal(0, windows.Disconnects);
        Assert.Equal(1, connections.LeaseCount(Share));
    }

    [Theory]
    [InlineData(86, "user name or password")]
    [InlineData(1326, "user name or password")]
    [InlineData(53, "was not found")]
    [InlineData(67, "was not found")]
    [InlineData(1219, "different account")]
    [InlineData(1909, "locked out")]
    [InlineData(5, "refused access")]
    public void A_refused_sign_in_says_why_in_plain_words(int win32Error, string expected)
    {
        var message = SAPServiceLayerClient.DescribeShareSignInFailure(Share, win32Error);

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.Contains(Share, message, StringComparison.Ordinal);
        Assert.Contains("Set-SapAttachmentsShareLogin.ps1", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlisted_refusal_carries_the_windows_wording_and_code()
    {
        // 1330: the account's password has expired.
        var message = SAPServiceLayerClient.DescribeShareSignInFailure(Share, 1330);

        Assert.Contains("(Windows error 1330)", message, StringComparison.Ordinal);
        Assert.Contains(new Win32Exception(1330).Message, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opt-in: runs the real Windows calls against a share this machine can already open under its own
    /// sign-in, and reads <c>net use</c> to see the entry Windows keeps. Set
    /// <c>SHOPINVENTORY_LIVE_SHARE</c> (for example <c>\\kfldb\b1_shf</c>) to run it; otherwise it
    /// passes without doing anything. Set it from PowerShell or cmd: Git Bash rewrites the backslashes
    /// on the way to the process, and Windows answers 67, network name not found.
    /// </summary>
    [Fact]
    public void Live_the_share_stays_connected_until_the_last_reader_leaves()
    {
        var share = Environment.GetEnvironmentVariable("SHOPINVENTORY_LIVE_SHARE");
        if (string.IsNullOrWhiteSpace(share) || !OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(IsListedByNetUse(share), $"{share} is already connected; disconnect it first.");

        // No account: Windows signs in as the current user, which is enough to see how it counts.
        var connections = NetworkShareConnections.Windows;
        var first = connections.Acquire(share, null, null);
        var second = connections.Acquire(share, null, null);
        Assert.True(IsListedByNetUse(share));

        first.Dispose();
        Assert.True(IsListedByNetUse(share), "The first reader out disconnected the share from under the second.");

        second.Dispose();
        Assert.False(IsListedByNetUse(share), "The last reader out left the share connected.");
    }

    private static bool IsListedByNetUse(string share)
    {
        using var netUse = Process.Start(new ProcessStartInfo("net", "use")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var output = netUse.StandardOutput.ReadToEnd();
        netUse.WaitForExit();

        return output
            .Split('\n')
            .Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => string.Equals(word, share, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeWindows
    {
        private int _connects;
        private int _disconnects;

        public int NextResult { get; set; } = NetworkShareConnections.NoError;

        public int Connects => Volatile.Read(ref _connects);

        public int Disconnects => Volatile.Read(ref _disconnects);

        public NetworkShareConnections Connections() => new(
            (_, _, _) =>
            {
                Interlocked.Increment(ref _connects);
                return NextResult;
            },
            _ =>
            {
                Interlocked.Increment(ref _disconnects);
                return NetworkShareConnections.NoError;
            });
    }
}
