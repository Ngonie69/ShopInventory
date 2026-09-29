using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.SalesOrders.Commands.BackfillSalesOrderCardNames;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Orders stored without a customer name get SAP's, in one batched read; a code SAP does not know is
/// not asked about again for a week; and a SAP failure is not mistaken for a missing customer.
/// </summary>
public sealed class SalesOrderCardNameBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _context;
    private readonly List<IReadOnlyCollection<string>> _asked = [];
    private Func<IReadOnlyCollection<string>, List<BusinessPartnerDto>> _sap = _ => [];

    public SalesOrderCardNameBackfillTests()
    {
        _connection.Open();
        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Every_code_that_needs_a_name_is_asked_about_in_one_batched_read()
    {
        AddOrder("SO-1", "C001", cardName: null);
        AddOrder("SO-2", "C001", cardName: "C001");
        AddOrder("SO-3", "C002", cardName: "");
        AddOrder("SO-4", "C003", cardName: "Already Named");
        await _context.SaveChangesAsync();
        _sap = codes => codes.Select(code => new BusinessPartnerDto { CardCode = code, CardName = $"Customer {code}" }).ToList();

        var result = await Run();

        Assert.Equal(["C001", "C002"], Assert.Single(_asked).Order());
        Assert.Equal(3, result.OrdersUpdated);
        Assert.Equal(
            ["Already Named", "Customer C001", "Customer C001", "Customer C002"],
            (await _context.SalesOrders.AsNoTracking().Select(order => order.CardName).ToListAsync()).Order());
    }

    [Fact]
    public async Task A_code_sap_does_not_know_is_not_asked_about_again_for_a_week()
    {
        AddOrder("SO-1", "GONE01", cardName: null);
        await _context.SaveChangesAsync();

        var first = await Run();
        Assert.Equal(1, first.CustomersUnresolved);

        _asked.Clear();
        var second = await Run();

        Assert.Empty(_asked);
        Assert.Equal(0, second.CustomersUnresolved);
    }

    [Fact]
    public async Task A_code_remembered_as_unknown_is_asked_about_again_after_a_week()
    {
        AddOrder("SO-1", "GONE01", cardName: null);
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = BackfillSalesOrderCardNamesHandler.UnresolvedConfigKey,
            Value = $$"""{"GONE01":"{{DateTime.UtcNow.AddDays(-8):O}}"}""",
            ValueType = "json",
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await Run();

        Assert.Equal(["GONE01"], Assert.Single(_asked));
    }

    [Fact]
    public async Task A_sap_failure_is_not_remembered_as_an_unknown_customer()
    {
        AddOrder("SO-1", "C001", cardName: null);
        await _context.SaveChangesAsync();
        _sap = _ => throw new TimeoutException("The SAP Service Layer did not respond in time.");

        var failed = await Handler().Handle(new BackfillSalesOrderCardNamesCommand(), CancellationToken.None);
        Assert.True(failed.IsError);

        _sap = codes => codes.Select(code => new BusinessPartnerDto { CardCode = code, CardName = "Found Later" }).ToList();
        _asked.Clear();
        var retried = await Run();

        Assert.Equal(["C001"], Assert.Single(_asked));
        Assert.Equal(1, retried.OrdersUpdated);
    }

    /// <summary>
    /// It used to run inline in startup, before readiness. Now it is a clustered job that one node
    /// runs once, a few minutes after starting, and only when SAP is switched on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void It_is_a_one_off_job_after_start_and_only_with_sap_on(bool sapEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SAP:Enabled"] = sapEnabled.ToString() })
            .Build();
        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        var quartz = provider.GetRequiredService<IOptions<QuartzOptions>>().Value;

        var job = quartz.JobDetails.SingleOrDefault(detail => detail.Key.Name == SalesOrderCardNameBackfillJob.JobName);
        var trigger = quartz.Triggers.SingleOrDefault(t => t.Key.Name == SalesOrderCardNameBackfillJob.StartupTriggerName);

        if (!sapEnabled)
        {
            Assert.Null(job);
            Assert.Null(trigger);
            return;
        }

        Assert.NotNull(job);
        Assert.True(job.Durable);
        var once = Assert.IsAssignableFrom<ISimpleTrigger>(trigger);
        Assert.Equal(0, once.RepeatCount);
        Assert.InRange(
            once.StartTimeUtc - DateTimeOffset.UtcNow,
            SalesOrderCardNameBackfillJob.StartupDelay - TimeSpan.FromSeconds(30),
            SalesOrderCardNameBackfillJob.StartupDelay);
    }

    private async Task<BackfillSalesOrderCardNamesResult> Run()
    {
        _context.ChangeTracker.Clear();
        var result = await Handler().Handle(new BackfillSalesOrderCardNamesCommand(), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private BackfillSalesOrderCardNamesHandler Handler() =>
        new(
            _context,
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetBusinessPartnersByCodesAsync) => Answer((IReadOnlyCollection<string>)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            }),
            NullLogger<BackfillSalesOrderCardNamesHandler>.Instance);

    private Task<List<BusinessPartnerDto>> Answer(IReadOnlyCollection<string> codes)
    {
        _asked.Add(codes.ToList());
        return Task.FromResult(_sap(codes));
    }

    /// <summary>SQLite has no row version to generate, so the tests supply one.</summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }

    private void AddOrder(string orderNumber, string cardCode, string? cardName) =>
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = cardCode,
            CardName = cardName,
            OrderDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            RowVersion = BitConverter.GetBytes(1L)
        });
}
