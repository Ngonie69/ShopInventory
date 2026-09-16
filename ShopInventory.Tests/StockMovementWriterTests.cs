using System.Text.RegularExpressions;

namespace ShopInventory.Tests;

/// <summary>
/// Who is allowed to move the figure the invoicing guard reads.
/// </summary>
/// <remarks>
/// <c>AvailableQuantity</c>'s setter is closed, so the only way to move a balance is
/// <c>DailyStockSnapshotItemEntity.Move</c>, and it is internal. That stops a stray assignment; it
/// does not stop a new caller, because every writer already lives in this assembly.
///
/// <para>So the list is written down here instead. It is short, each entry has a reason, and a new
/// caller fails this test — which is the actual guarantee on offer. Not that the journal cannot be
/// bypassed, but that bypassing it is a deliberate edit to a file named "writer tests" rather than
/// something that happens quietly in a handler three directories away and is noticed a quarter
/// later, when a figure nobody can account for turns up at a till.</para>
///
/// <para>Adding to this list is allowed. Adding to it without also journalling what you moved is
/// what the invariant tests are for — see <see cref="StockMovementInvariantTests"/>.</para>
/// </remarks>
public sealed class StockMovementWriterTests
{
    /// <summary>
    /// Every file permitted to move a snapshot row's balance, and what accounts for its movements.
    /// </summary>
    private static readonly Dictionary<string, string> Permitted = new()
    {
        ["Services/StockLedger.cs"] =
            "Sales. Journals Commit, Settle and Release in the same SaveChanges as the balance.",

        ["Common/Stock/UnpostedTillSales.cs"] =
            "The shared FEFO draw-down. Journals nothing itself and must not be called by anything "
            + "that does not — it returns the signed amount it moved precisely so its caller can "
            + "record it.",

        ["Features/DesktopIntegration/Commands/ProcessTransferEvent/ProcessTransferEventHandler.cs"] =
            "The listener's webhook. Journals Transfer, keyed on the SAP document entry.",

        ["Services/Jobs/StockLedgerDivergenceJob.cs"] =
            "The hourly comparison and the manual refresh behind it. Journals Reconciliation.",
    };

    [Fact]
    public void Only_the_listed_writers_move_a_snapshot_balance()
    {
        var actual = FilesCalling(new Regex(@"\.Move\("));

        // Named rather than counted: a diff that swaps one writer for another keeps the count and is
        // exactly the change worth seeing.
        Assert.Equal(Permitted.Keys.OrderBy(path => path), actual.OrderBy(path => path));
    }

    [Fact]
    public void The_balance_keeps_its_closed_setter()
    {
        // Everything above rests on the setter being closed: widen it and a stray assignment
        // compiles again, from anywhere, and every other test in this suite still passes. So the
        // declaration itself is the assertion.
        //
        // Read from the file rather than from reflection on purpose. Reflection reports a private
        // setter and an init-only one identically, and EF materialises through either, so the check
        // would go on passing over a change that reopened the property to object initializers.
        var declaration = File.ReadAllText(Path.Combine(
            SourceRoot(), "ShopInventory", "Models", "Entities", "DailyStockSnapshotItemEntity.cs"));

        Assert.Matches(
            new Regex(@"public decimal AvailableQuantity \{ get; private set; \}"),
            declaration);
    }

    private static List<string> FilesCalling(Regex pattern)
    {
        var root = SourceRoot();

        return Directory
            .EnumerateFiles(Path.Combine(root, "ShopInventory"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path
                .GetRelativePath(Path.Combine(root, "ShopInventory"), path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
    }

    /// <summary>
    /// The repository root, found by walking up from the test binary until the solution turns up.
    /// </summary>
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find ShopInventory.sln above {AppContext.BaseDirectory}. This test reads the "
            + "source tree, so it needs to be run from a working copy rather than from published output.");
    }
}
