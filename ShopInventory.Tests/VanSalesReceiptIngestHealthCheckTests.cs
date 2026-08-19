using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Health;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the one path by which a van handset that has stopped delivering receipts to ZIMRA reaches a
/// human. Every condition here is already detected and logged by
/// <see cref="VanSalesSignedReceiptIngestService"/>; what this check adds is the alert, so a
/// regression that quietly downgrades one of these to Healthy puts the fleet back to silent failure.
/// </summary>
public sealed class VanSalesReceiptIngestHealthCheckTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly ServiceProvider _services;

    public VanSalesReceiptIngestHealthCheckTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddScoped(_ => _context);
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Reports_healthy_when_nothing_is_waiting()
    {
        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_healthy_when_fiscalisation_is_switched_off()
    {
        await AddSaleAsync("VAN-1", DesktopSaleReceiptIngestStatus.ChainBroken);

        var result = await CheckAsync(enabled: false);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// One row is enough. A chain break stops that handset's whole day and nothing drains it on its
    /// own, so Degraded would be the wrong severity — it has to page someone.
    /// </summary>
    [Fact]
    public async Task Reports_unhealthy_for_a_single_chain_break()
    {
        await AddSaleAsync("VAN-1", DesktopSaleReceiptIngestStatus.ChainBroken, deviceId: 36189);

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("chain break", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Data["chainBroken"]);
        Assert.Equal(1, result.Data["stoppedDevices"]);
    }

    [Fact]
    public async Task Reports_unhealthy_for_an_unsignable_receipt()
    {
        await AddSaleAsync("VAN-1", DesktopSaleReceiptIngestStatus.Unsignable, deviceId: 36189);

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.Data["unsignable"]);
    }

    [Fact]
    public async Task Reports_unhealthy_once_a_receipt_has_spent_every_attempt()
    {
        await AddSaleAsync(
            "VAN-1",
            DesktopSaleReceiptIngestStatus.Failed,
            deviceId: 36189,
            attempts: VanSalesSignedReceiptIngestService.MaxIngestAttempts);

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.Data["retriesExhausted"]);
    }

    /// <summary>
    /// A failure with attempts left is what the next run is for. Alerting on it would fire on every
    /// transient network blip and train the recipients to ignore the alert that matters.
    /// </summary>
    [Fact]
    public async Task Stays_healthy_while_a_failed_receipt_still_has_attempts_left()
    {
        await AddSaleAsync(
            "VAN-1",
            DesktopSaleReceiptIngestStatus.Failed,
            deviceId: 36189,
            attempts: VanSalesSignedReceiptIngestService.MaxIngestAttempts - 1);

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// The unit of damage is the handset, not the receipt: a stopped van holds its whole queue, so a
    /// stack of receipts behind one break is still one van to go and fix.
    /// </summary>
    [Fact]
    public async Task Counts_stopped_handsets_rather_than_blocked_receipts()
    {
        await AddSaleAsync("VAN-1", DesktopSaleReceiptIngestStatus.ChainBroken, deviceId: 36189);
        await AddSaleAsync("VAN-2", DesktopSaleReceiptIngestStatus.Unsignable, deviceId: 36189);
        await AddSaleAsync("VAN-3", DesktopSaleReceiptIngestStatus.ChainBroken, deviceId: 35410);

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(2, result.Data["stoppedDevices"]);
    }

    [Fact]
    public async Task Reports_degraded_when_a_pending_receipt_outlives_the_drain_interval()
    {
        await AddSaleAsync(
            "VAN-1",
            DesktopSaleReceiptIngestStatus.Pending,
            deviceId: 36189,
            createdAt: DateTime.UtcNow.AddMinutes(-45));

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_a_pending_receipt_is_old_enough_to_miss_its_fiscal_day()
    {
        await AddSaleAsync(
            "VAN-1",
            DesktopSaleReceiptIngestStatus.Pending,
            deviceId: 36189,
            createdAt: DateTime.UtcNow.AddHours(-3));

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Stays_healthy_while_a_fresh_receipt_is_draining_normally()
    {
        await AddSaleAsync("VAN-1", DesktopSaleReceiptIngestStatus.Pending, deviceId: 36189);

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// An unstamped sale consumed no receipt number, so nothing waits behind it and no device is
    /// stopped. The drain skips it; counting it here would alert on a handset that needs an app
    /// update, not a reconciliation.
    /// </summary>
    [Fact]
    public async Task Ignores_unstamped_sales()
    {
        await AddSaleAsync(
            "VAN-1",
            DesktopSaleReceiptIngestStatus.Unstamped,
            deviceId: 36189,
            createdAt: DateTime.UtcNow.AddDays(-2));

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Ignores_sales_that_are_not_van_sales()
    {
        await AddSaleAsync(
            "POS-1",
            DesktopSaleReceiptIngestStatus.ChainBroken,
            deviceId: 36189,
            sourceSystem: "SomeOtherTill");

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Ignores_receipts_the_platform_has_already_archived()
    {
        await AddSaleAsync(
            "VAN-1",
            DesktopSaleReceiptIngestStatus.Ingested,
            deviceId: 36189,
            createdAt: DateTime.UtcNow.AddDays(-2));

        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private async Task<HealthCheckResult> CheckAsync(bool enabled = true)
    {
        var check = new VanSalesReceiptIngestHealthCheck(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new FiscalisationSettings { Enabled = enabled }));

        return await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    private async Task AddSaleAsync(
        string reference,
        DesktopSaleReceiptIngestStatus status,
        int? deviceId = null,
        int attempts = 0,
        DateTime? createdAt = null,
        string sourceSystem = SaleSourceSystems.VanSales)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            CardCode = "C0001",
            WarehouseCode = "WH01",
            Currency = "USD",
            DocDate = DateTime.UtcNow.Date,
            SourceSystem = sourceSystem,
            FiscalDeviceId = deviceId,
            ReceiptIngestStatus = status,
            ReceiptIngestAttempts = attempts,
            CreatedAt = createdAt ?? DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
    }
}
