using System.Text.RegularExpressions;

namespace ShopInventory.Tests;

/// <summary>
/// Every execution of a stored SAP query has to go out through the client's transient retry, and
/// this checks the source rather than the behaviour because there are nineteen of them.
/// </summary>
/// <remarks>
/// The shape is copy-pasted: build a <c>SQLQueries('code')/List</c> URL, send, re-send once on 401.
/// Whoever writes the twentieth will copy the nineteenth, and if that one sends bare the loss is
/// invisible until SAP drops a handshake in production — which is exactly how the sales order vs
/// invoice report failed on 2026-08-07, on the one leg of the SQL path that had never been wrapped.
/// A test per call site would need a fake, a public entry point and a two-second backoff each;
/// scanning for the pattern covers all of them and catches the next one before it ships.
/// </remarks>
public class SapStoredQueryRetryTests
{
    private const string ClientPath = "ShopInventory/Services/SAPServiceLayerClient.cs";

    /// <summary>How far past the URL to look for the send it belongs to.</summary>
    private const int SendSearchWindow = 30;

    private static readonly Regex StoredQueryExecuteUrl = new(@"SQLQueries\('\{[^}]+\}'\)/List");

    /// <summary>
    /// <c>SendPriceListRequestWithBudgetAsync</c> counts: it is the retry helper with a deadline
    /// around it, not a way past it.
    /// </summary>
    private static readonly Regex RetriedSend =
        new(@"SendSapRequestWithTransientRetryAsync\(|SendPriceListRequestWithBudgetAsync\(");

    private static readonly Regex BareSend = new(@"\bawait\s+_?\w*[Cc]lient\.SendAsync\(");

    [Fact]
    public void Every_stored_query_execution_goes_through_the_transient_retry()
    {
        var source = File.ReadAllLines(Path.Combine(RepositoryRoot(), ClientPath));

        var executeSites = Enumerable
            .Range(0, source.Length)
            .Where(index => StoredQueryExecuteUrl.IsMatch(source[index]))
            .ToList();

        // If this ever reads zero the scan has stopped finding anything and the guard is inert.
        Assert.NotEmpty(executeSites);

        var offenders = new List<string>();

        foreach (var index in executeSites)
        {
            var (kind, sendLine) = ClassifySend(source, index);

            if (kind == SendKind.Retried)
            {
                continue;
            }

            offenders.Add(kind == SendKind.Bare
                ? $"{ClientPath}:{sendLine + 1}: {source[sendLine].Trim()}"
                : $"{ClientPath}:{index + 1}: no send found within {SendSearchWindow} lines of "
                  + $"{source[index].Trim()}");
        }

        Assert.True(
            offenders.Count == 0,
            "These stored-query executions send without the transient retry, so one dropped "
            + "connection fails the whole caller:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static (SendKind Kind, int Line) ClassifySend(string[] source, int urlLine)
    {
        var limit = Math.Min(source.Length, urlLine + SendSearchWindow);

        for (var index = urlLine; index < limit; index++)
        {
            if (RetriedSend.IsMatch(source[index]))
            {
                return (SendKind.Retried, index);
            }

            if (BareSend.IsMatch(source[index]))
            {
                return (SendKind.Bare, index);
            }
        }

        return (SendKind.None, urlLine);
    }

    private enum SendKind
    {
        Retried,
        Bare,
        None
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
