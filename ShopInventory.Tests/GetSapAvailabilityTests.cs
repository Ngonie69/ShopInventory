using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.Sync.Queries.GetSapAvailability;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the staff banner is told: SAP is down and how many sales are waiting, or SAP is back and how
/// many are still posting.
/// </summary>
public sealed class GetSapAvailabilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly SapAvailability _availability;
    private int _nextSale;

    public GetSapAvailabilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _availability = new SapAvailability(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SapAvailability>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task With_sap_up_and_no_recent_outage_there_is_nothing_to_show()
    {
        await GivenSaleAsync(createdMinutesAgo: 5);

        Assert.Equal(SapAvailabilityResult.Available, await AskAsync());
    }

    [Fact]
    public async Task During_an_outage_it_counts_the_sales_since_it_began_that_sap_does_not_have()
    {
        var since = DateTime.UtcNow.AddHours(-2);
        _availability.Apply(new SapAvailabilityState(7, since, SapOutageCauses.Unreachable));

        await GivenSaleAsync(createdMinutesAgo: 60);                                  // waiting
        await GivenSaleAsync(createdMinutesAgo: 30, source: SaleSourceSystems.VanSalesOnline,
            status: DesktopSaleConsolidationStatus.Consolidated);                    // online: Consolidated, still waiting
        await GivenSaleAsync(createdMinutesAgo: 20, sapDocEntry: 501);               // posted
        await GivenSaleAsync(createdMinutesAgo: 10, status: DesktopSaleConsolidationStatus.Excluded);
        await GivenSaleAsync(createdMinutesAgo: 180);                                 // before the outage

        var answer = await AskAsync();

        Assert.True(answer.IsDown);
        Assert.Equal(SapOutageCauses.Unreachable, answer.Cause);
        Assert.Equal(since, answer.SinceUtc);
        Assert.Null(answer.EndedAtUtc);
        Assert.Equal(2, answer.SalesAwaitingSap);
    }

    [Fact]
    public async Task Just_after_an_outage_it_says_sap_is_back_and_what_is_still_posting()
    {
        var started = DateTime.UtcNow.AddHours(-3);
        var ended = DateTime.UtcNow.AddMinutes(-10);
        await GivenOutageAsync(started, ended);
        await GivenSaleAsync(createdMinutesAgo: 120);

        var answer = await AskAsync();

        Assert.False(answer.IsDown);
        Assert.Equal(started, answer.SinceUtc);
        Assert.Equal(ended, answer.EndedAtUtc);
        Assert.Equal(1, answer.SalesAwaitingSap);
    }

    [Fact]
    public async Task An_outage_that_ended_over_half_an_hour_ago_is_no_longer_shown()
    {
        await GivenOutageAsync(DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddMinutes(-45));

        Assert.Equal(SapAvailabilityResult.Available, await AskAsync());
    }

    private async Task<SapAvailabilityResult> AskAsync()
    {
        var result = await new GetSapAvailabilityHandler(_context, _availability)
            .Handle(new GetSapAvailabilityQuery(), default);
        Assert.False(result.IsError);
        return result.Value;
    }

    private async Task GivenOutageAsync(DateTime started, DateTime ended)
    {
        _context.SapOutages.Add(new SapOutageEntity
        {
            StartedAtUtc = started,
            DeclaredAtUtc = started.AddMinutes(1),
            EndedAtUtc = ended,
            Cause = SapOutageCauses.Unreachable,
            LastProbeAtUtc = ended
        });
        await _context.SaveChangesAsync();
    }

    private async Task GivenSaleAsync(
        int createdMinutesAgo,
        string source = SaleSourceSystems.ShopTill,
        DesktopSaleConsolidationStatus status = DesktopSaleConsolidationStatus.Pending,
        int? sapDocEntry = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"S-{++_nextSale}",
            SourceSystem = source,
            CardCode = "KEFSHOP-BP",
            WarehouseCode = "KEFSHOP",
            Currency = "USD",
            CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo),
            ConsolidationStatus = status,
            SapDocEntry = sapDocEntry
        });
        await _context.SaveChangesAsync();
    }
}
