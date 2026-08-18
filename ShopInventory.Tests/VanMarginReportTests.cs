using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the local half of the margin report.
///
/// The report is named for a figure it does not yet carry, which makes its failure mode unusual: the
/// danger is not a wrong margin but a plausible-looking one. Every margin field is null and has to
/// stay null — a zero would report an item sold at exactly cost, which is a finding rather than an
/// absence, and it would be indistinguishable from the real thing once the cost source is connected.
///
/// The measure the report does carry is the costable share: what fraction of van revenue SAP is even
/// in a position to price. Its way of being wrong flatters — a line whose sale never posted is not
/// costable however real its revenue, and counting it would overstate what this report can ever
/// deliver. Both halves of the union post by different routes and on differently-named columns, so
/// each is pinned separately; reading only the offline column would report the whole online half as
/// permanently unpriceable.
/// </summary>
public sealed class VanMarginReportTests : IDisposable
{
    private const string VanAccount = "VAN010";
    private const string VanWarehouse = "VAN010";

    private static readonly Guid Rep = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanMarginReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = VanWarehouse,
            AssignedBusinessPartnerCode = VanAccount
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The absent half ---

    /// <summary>
    /// The margin fields are null on every row and stay null. A zero would say the vans sell at
    /// cost, and it would be indistinguishable from a real result once the cost source lands.
    /// </summary>
    [Fact]
    public async Task Margin_is_null_and_never_zero()
    {
        AddSale(Rep, "S1", 100m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.False(report.Summary.MarginAvailable);

        var item = Assert.Single(report.Items);
        Assert.Null(item.UnitCost);
        Assert.Null(item.CostByCurrency);
        Assert.Null(item.MarginByCurrency);
    }

    /// <summary>
    /// The report can never report itself complete. It is named for a figure it does not carry, and a
    /// clean bill of health would be a lie by omission.
    /// </summary>
    [Fact]
    public async Task The_report_never_calls_itself_clean()
    {
        AddSale(Rep, "S1", 100m, new DateTime(2026, 8, 4), posted: true);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.False(report.Quality.IsClean);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("Margin is not computed"));
    }

    // --- The costable share ---

    /// <summary>
    /// A sale that never reached SAP can never be costed, however real its revenue. This is the
    /// figure the whole report exists to publish.
    /// </summary>
    [Fact]
    public async Task A_sale_that_never_reached_sap_is_not_costable()
    {
        AddSale(Rep, "POSTED", 100m, new DateTime(2026, 8, 4), posted: true);
        AddSale(Rep, "HELD", 60m, new DateTime(2026, 8, 5), posted: false);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.LineCount);
        Assert.Equal(1, report.Summary.PostedLineCount);
        Assert.Equal(0.5, report.Summary.CostableLineShare);

        // The revenue splits the same way, and never folds across the two.
        Assert.Equal(160m, Assert.Single(report.Summary.RevenueByCurrency).Gross);
        Assert.Equal(100m, Assert.Single(report.Summary.CostableRevenueByCurrency).Gross);

        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("has not reached SAP"));
    }

    /// <summary>
    /// A van sale line always carries a warehouse, and the report depends on that: the warehouse is
    /// the key the cost will join on.
    ///
    /// This started as a test that a warehouse-less line is reported as uncostable, and it could not
    /// be written — the database refuses the row. Both line entities mark the column required, and
    /// the offline ingest refuses an entire batch from a rep with no assigned warehouse rather than
    /// accepting one. So the report carries no "lines with no warehouse" figure: it could never be
    /// anything but zero, and a counter that cannot move reads as a check that passed rather than
    /// one that cannot fail. This pins the guarantee the absence rests on.
    /// </summary>
    [Fact]
    public async Task A_line_cannot_be_written_without_a_warehouse()
    {
        AddSale(Rep, "NOWHERE", 100m, new DateTime(2026, 8, 4), posted: true, warehouse: null);

        await Assert.ThrowsAnyAsync<Exception>(() => _context.SaveChangesAsync());
    }

    /// <summary>
    /// The online half posts by its own route and on its own column, so a confirmed reservation
    /// carrying a SAP document number is costable exactly as an offline sale is. Reading only the
    /// offline column would report the online half as permanently unpriceable.
    /// </summary>
    [Fact]
    public async Task An_online_sale_that_posted_is_costable_too()
    {
        AddReservation(Rep, "ONLINE", 80m, new DateTime(2026, 8, 6, 9, 0, 0), sapDocNum: 4242);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.LineCount);
        Assert.Equal(1, report.Summary.PostedLineCount);
        Assert.Equal(1.0, report.Summary.CostableLineShare);
    }

    [Fact]
    public async Task An_online_sale_that_never_posted_is_not_costable()
    {
        AddReservation(Rep, "ONLINE", 80m, new DateTime(2026, 8, 6, 9, 0, 0), sapDocNum: null);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.LineCount);
        Assert.Equal(0, report.Summary.PostedLineCount);
    }

    /// <summary>
    /// Nothing sold means no costable share — not a share of zero, which would read as an estate
    /// whose posting has completely failed rather than one that had a quiet week.
    /// </summary>
    [Fact]
    public async Task An_empty_period_has_no_costable_share_rather_than_a_share_of_zero()
    {
        var report = await RunAsync();

        Assert.Equal(0, report.Summary.LineCount);
        Assert.Null(report.Summary.CostableLineShare);
        Assert.Empty(report.Items);
        Assert.Empty(report.Vans);
        Assert.NotEmpty(report.Quality.Caveats);
    }

    // --- The posting switch ---

    /// <summary>
    /// With the posting job off, the offline half can never be costed however long it waits. That is
    /// a different statement from "these particular sales have not posted yet", and the page has to
    /// make it.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_posting_switch_is_reported_alongside_the_costable_share(
        bool enabled,
        bool expectCaveat)
    {
        AddSale(Rep, "HELD", 60m, new DateTime(2026, 8, 5), posted: false);
        await _context.SaveChangesAsync();

        var report = await RunAsync(postingEnabled: enabled);

        Assert.Equal(enabled, report.Quality.PostingJobEnabled);
        Assert.Equal(
            expectCaveat,
            report.Quality.Caveats.Any(caveat => caveat.Contains("posting job is switched off")));
    }

    // --- Grouping ---

    /// <summary>
    /// Money is per currency and never folded. A USD line and a ZiG line are two rows on every
    /// revenue figure the report publishes.
    /// </summary>
    [Fact]
    public async Task Two_currencies_never_become_one_revenue_figure()
    {
        AddSale(Rep, "USD1", 100m, new DateTime(2026, 8, 4));
        AddSale(Rep, "ZIG1", 4000m, new DateTime(2026, 8, 5), currency: "ZWG");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.RevenueByCurrency.Count);
        Assert.Equal(100m, report.Summary.RevenueByCurrency.Single(m => m.Currency == "USD").Gross);
        Assert.Equal(4000m, report.Summary.RevenueByCurrency.Single(m => m.Currency == "ZWG").Gross);
    }

    /// <summary>
    /// The van rows account for every penny in the summary. Since every line carries a warehouse
    /// there is no bucket for the ones that do not, so any shortfall here would be a grouping bug
    /// rather than a disclosed gap — which is why the totals are asserted against each other.
    /// </summary>
    [Fact]
    public async Task The_van_rows_account_for_all_the_revenue()
    {
        AddSale(Rep, "A", 100m, new DateTime(2026, 8, 4), warehouse: "VAN010");
        AddSale(Rep, "B", 40m, new DateTime(2026, 8, 5), warehouse: "VAN011");
        AddSale(Rep, "C", 25m, new DateTime(2026, 8, 6), warehouse: "VAN011");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Vans.Count);
        Assert.Equal(2, report.Summary.VanCount);

        Assert.Equal(
            report.Summary.RevenueByCurrency.Sum(money => money.Gross),
            report.Vans.Sum(van => van.RevenueByCurrency.Sum(money => money.Gross)));

        // Biggest van first — this is a ranking, and the busiest row is the one a reader wants.
        Assert.Equal("VAN010", report.Vans[0].WarehouseCode);
    }

    /// <summary>An item sold from two vans is one item row that knows it spans two.</summary>
    [Fact]
    public async Task An_item_sold_from_two_vans_reports_both()
    {
        AddSale(Rep, "A", 100m, new DateTime(2026, 8, 4), warehouse: "VAN010");
        AddSale(Rep, "B", 60m, new DateTime(2026, 8, 5), warehouse: "VAN011");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(2, item.VanCount);
        Assert.Equal(2, item.LineCount);
        Assert.Equal(160m, Assert.Single(item.RevenueByCurrency).Gross);
    }

    // --- Validation ---

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_rejected()
    {
        var result = await RunRawAsync(From, From.AddDays(-1));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    // --- Harness ---

    private async Task<VanMarginReportResult> RunAsync(bool postingEnabled = false)
    {
        var result = await RunRawAsync(From, To, postingEnabled);

        Assert.False(result.IsError);
        return result.Value;
    }

    private Task<ErrorOr.ErrorOr<VanMarginReportResult>> RunRawAsync(
        DateTime from,
        DateTime to,
        bool postingEnabled = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VanSalesPosting:Enabled"] = postingEnabled ? "true" : "false"
            })
            .Build();

        return new GetVanMarginReportHandler(_context, configuration).Handle(
            new GetVanMarginReportQuery(from, to),
            CancellationToken.None);
    }

    private void AddSale(
        Guid userId,
        string reference,
        decimal total,
        DateTime docDate,
        bool posted = false,
        string? warehouse = VanWarehouse,
        string currency = "USD",
        string itemCode = "CHE011") =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = VanAccount,
            CardName = VanAccount,
            RouteCustomerCode = "SHOP1",
            RouteCustomerName = "Shop One",
            DocDate = docDate,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            // Deliberately nullable here. The column is required and one test passes null to prove
            // the database refuses the row — which is what the report's missing "no warehouse"
            // counter rests on.
            WarehouseCode = warehouse!,
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = userId.ToString(),
            SapDocNum = posted ? 9001 : null,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = warehouse!
                }
            ]
        });

    private void AddReservation(
        Guid userId,
        string reference,
        decimal total,
        DateTime createdAtUtc,
        int? sapDocNum,
        string? warehouse = VanWarehouse,
        string itemCode = "CHE011") =>
        _context.StockReservations.Add(new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = VanAccount,
            RouteCustomerCode = "SHOP1",
            RouteCustomerName = "Shop One",
            Status = ReservationStatus.Confirmed,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddMinutes(5),
            TotalValue = total,
            Currency = "USD",
            PaymentMethod = "Cash",
            CreatedBy = userId.ToString(),
            SAPDocNum = sapDocNum,
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    OriginalQuantity = 1m,
                    ReservedQuantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = warehouse!
                }
            ]
        });
}
