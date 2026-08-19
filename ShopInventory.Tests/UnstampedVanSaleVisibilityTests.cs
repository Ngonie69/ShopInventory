using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Where an operator finds out that a van is trading on a handset that cannot stamp receipts.
/// </summary>
/// <remarks>
/// This is a regression in the strict sense: before the signing work, a van sale that arrived unstamped
/// was written <see cref="DesktopSaleReceiptIngestStatus.Unsignable"/> and therefore appeared in the
/// Exception Center. Giving it a status of its own was right — it took no receipt number, so it holds no
/// place in any chain and must not stop a device the way a chain hole does — but the new status was not
/// added to the Exception Center's predicate, and the row silently left the surface people work from.
///
/// <para>
/// So there are two things to hold at once, and they pull in opposite directions. It has to be visible,
/// because a ZIMRA device making unstamped sales in the field is exactly what the rollout switch exists to
/// end. And it has to be visibly <i>not</i> a chain hole, because the remedy is an app update on one
/// handset rather than a reconciliation, and nothing is queued behind it.
/// </para>
/// </remarks>
public sealed class UnstampedVanSaleVisibilityTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 8, 10);

    private const int DeviceNumber = 35410;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public UnstampedVanSaleVisibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The regression itself. An unstamped sale showed only on the fiscalisation console, which is a
    /// status page rather than the queue of work that needs doing.
    /// </summary>
    [Fact]
    public async Task An_unstamped_van_sale_is_listed_in_the_exception_center()
    {
        AddSale("VAN006-INV-20260810-BBB222", DesktopSaleReceiptIngestStatus.Unstamped, globalNo: null);
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());

        Assert.Equal(ExceptionCenterSources.FiscalReceiptIngest, item.Source);
        Assert.Equal("VAN006-INV-20260810-BBB222", item.Reference);
        Assert.Equal(nameof(DesktopSaleReceiptIngestStatus.Unstamped), item.Status);
    }

    /// <summary>
    /// Told apart from a chain hole in the words, not merely in a status code nobody reads. A title
    /// describing something stuck would have it worked as a reconciliation it cannot be — there is no
    /// receipt to reconcile.
    /// </summary>
    [Fact]
    public async Task It_reads_as_a_handset_to_update_rather_than_a_receipt_to_reconcile()
    {
        AddSale("VAN006-INV-20260810-BBB222", DesktopSaleReceiptIngestStatus.Unstamped, globalNo: null);
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());

        // The title names the handset, and does not claim a receipt is stuck.
        Assert.Contains("handset", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chain", item.Title, StringComparison.OrdinalIgnoreCase);

        // The text leads with what is not at stake, because that is the half that gets assumed wrong.
        Assert.Contains("Nothing is blocked", item.LastError);
        Assert.Contains("update", item.LastError, StringComparison.OrdinalIgnoreCase);

        // Retry stays off across this source: there is nothing to resend.
        Assert.False(item.CanRetry);
    }

    /// <summary>
    /// The other side of the same distinction, and the reason the status was split out in the first
    /// place. A real chain hole still reads as one, and the two are not collapsed into one message now
    /// that they share a list.
    /// </summary>
    [Fact]
    public async Task A_real_chain_hole_still_reads_as_one()
    {
        AddSale("VAN006-INV-20260810-BBB222", DesktopSaleReceiptIngestStatus.Unstamped, globalNo: null);
        AddSale("VAN006-INV-20260810-CCC333", DesktopSaleReceiptIngestStatus.Unsignable, globalNo: 503);
        await _context.SaveChangesAsync();

        var items = await LoadAsync();
        Assert.Equal(2, items.Count);

        var hole = Assert.Single(items, item => item.Reference == "VAN006-INV-20260810-CCC333");
        Assert.Contains("signature", hole.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing is blocked", hole.LastError);
    }

    /// <summary>
    /// Only the Exception Center changes. The drain must still skip an unstamped row — there is nothing
    /// signed to send — and the fiscal day must still count it as settled rather than outstanding, or one
    /// un-updated handset would hold its device's day open indefinitely.
    /// </summary>
    [Fact]
    public async Task Listing_it_does_not_make_it_outstanding_anywhere_else()
    {
        AddSale("VAN006-INV-20260810-BBB222", DesktopSaleReceiptIngestStatus.Unstamped, globalNo: null);
        await _context.SaveChangesAsync();

        Assert.Single(await LoadAsync());

        // The drain's own selection, unchanged: nothing to hand over.
        var drainable = await _context.DesktopSales.CountAsync(
            sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Pending
                    || sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Failed);
        Assert.Equal(0, drainable);

        // And the fiscal day's, likewise: an unstamped sale consumed no receipt number, so the day is not
        // short of one and can still close.
        var outstanding = await _context.DesktopSales.CountAsync(
            sale => sale.FiscalDeviceId == DeviceNumber
                    && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested
                    && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unstamped
                    && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable);
        Assert.Equal(0, outstanding);
    }

    /// <summary>
    /// Three places told an operator to watch a "van sales exceptions report" until the unstamped count
    /// reached zero, and no such report was ever built — there is nothing under
    /// <c>Features/VanSalesReports/Queries/</c> that counts them. A named operational control that does
    /// not exist is worse than none: it is the instruction someone follows during a compliance rollout.
    /// </summary>
    /// <remarks>
    /// A text assertion because the defect was text. The surfaces that do exist are the fiscalisation
    /// console and the Exception Center, both pinned by the tests above.
    /// </remarks>
    [Theory]
    [InlineData("ShopInventory/appsettings.json")]
    [InlineData("ShopInventory/Configuration/FiscalisationSettings.cs")]
    [InlineData("ShopInventory/Models/Entities/DesktopSaleEntity.cs")]
    public void No_source_still_points_an_operator_at_a_report_that_does_not_exist(string relativePath)
    {
        var path = Path.Combine(SolutionRoot(), relativePath);
        Assert.True(File.Exists(path), $"{relativePath} has moved; update this test with it.");

        Assert.DoesNotContain(
            "van sales exceptions report",
            File.ReadAllText(path),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private Task<List<ExceptionCenterItemDto>> LoadAsync()
        => GetExceptionCenterHandler.LoadFiscalReceiptIngestFailuresAsync(_context, 750, default);

    private void AddSale(string reference, DesktopSaleReceiptIngestStatus status, int? globalNo)
        => _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            DocDate = Day,
            TotalAmount = 100m,
            VatAmount = 13.42m,
            AmountPaid = 100m,
            Currency = "USD",
            WarehouseCode = "VAN006",
            CostCentreCode = "CC006",
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,

            // Failed, not Success: an unstamped sale printed no receipt of its own, which is the whole
            // reason it is worth surfacing.
            FiscalizationStatus = status == DesktopSaleReceiptIngestStatus.Unstamped
                ? DesktopSaleFiscalizationStatus.Failed
                : DesktopSaleFiscalizationStatus.Success,
            FiscalDeviceId = DeviceNumber,
            FiscalDayNo = "19",
            ReceiptGlobalNo = globalNo,
            ReceiptIngestStatus = status,
            CreatedAt = Day
        });

    /// <summary>Walks up from the test binaries until the solution file is in sight.</summary>
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
