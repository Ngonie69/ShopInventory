using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Desktop-sale incoming payments are switched from Web → Settings: a <c>SystemConfigs</c> row the job
/// reads on every run, falling back to configuration (off) until someone saves it.
/// </summary>
public sealed class DailyIncomingPaymentSwitchTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DailyIncomingPaymentSwitchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Unsaved_switch_falls_back_to_configuration()
    {
        Assert.False((await NewSwitch(configured: false).GetAsync()).Enabled);
        Assert.True((await NewSwitch(configured: true).GetAsync()).Enabled);
    }

    [Fact]
    public async Task Saved_switch_outranks_configuration_both_ways()
    {
        await NewSwitch(configured: false).SetAsync(true);
        var on = await NewSwitch(configured: false).GetAsync();
        Assert.True(on.Enabled);
        Assert.NotNull(on.UpdatedAtUtc);

        await NewSwitch(configured: true).SetAsync(false);
        Assert.False((await NewSwitch(configured: true).GetAsync()).Enabled);

        await using var context = NewContext();
        Assert.Single(context.SystemConfigs, c => c.Key == DailyIncomingPaymentSwitch.ConfigKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Job_runs_the_payment_service_only_when_switched_on(bool enabled, bool expectRun)
    {
        await NewSwitch(configured: !enabled).SetAsync(enabled);

        var serviceResolved = false;
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton(Options.Create(new DesktopSalePostingSettings()));
        services.AddScoped<DailyIncomingPaymentSwitch>();
        services.AddScoped<DailyIncomingPaymentService>(_ =>
        {
            serviceResolved = true;
            throw new InvalidOperationException("Stand-in: the payment pass would have run.");
        });

        using var provider = services.BuildServiceProvider();
        var job = new DailyIncomingPaymentJob(provider, NullLogger<DailyIncomingPaymentJob>.Instance);

        await job.Execute(StubProxy.For<IJobExecutionContext>((method, _) => method.Name switch
        {
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new InvalidOperationException($"Unexpected Quartz call: {method.Name}")
        }));

        Assert.Equal(expectRun, serviceResolved);
    }

    [Fact]
    public void Payment_job_stays_declared_so_switching_on_needs_no_restart()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SAP:Enabled"] = "true" })
            .Build();

        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        var jobs = provider.GetRequiredService<IOptions<QuartzOptions>>().Value.JobDetails.Select(j => j.Key.Name);

        Assert.Contains("daily-incoming-payment", jobs);
    }

    [Fact]
    public void Shipped_appsettings_keep_payments_off()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "ShopInventory", "appsettings.json"))
            .Build();

        var settings = configuration.GetSection(DesktopSalePostingSettings.SectionName)
            .Get<DesktopSalePostingSettings>()!;

        Assert.False(settings.DailyPaymentEnabled);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private DailyIncomingPaymentSwitch NewSwitch(bool configured) =>
        new(NewContext(), Options.Create(new DesktopSalePostingSettings { DailyPaymentEnabled = configured }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShopInventory.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
