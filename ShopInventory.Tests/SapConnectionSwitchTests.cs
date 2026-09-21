using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Health;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Settings → SAP Connection switch: a <c>SystemConfigs</c> row that, when off, makes the circuit
/// read as open, so no request reaches SAP and every caller that queues during an outage queues.
/// </summary>
public sealed class SapConnectionSwitchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public SapConnectionSwitchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        _provider = services.BuildServiceProvider();

        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Unsaved_switch_is_on()
    {
        var state = await NewSwitch().RefreshAsync();

        Assert.True(state.Enabled);
        Assert.Null(state.UpdatedAtUtc);
    }

    [Fact]
    public async Task A_saved_switch_reaches_another_node_on_its_next_refresh()
    {
        var saver = NewSwitch();
        var otherNode = NewSwitch();
        await otherNode.RefreshAsync();

        await saver.SetAsync(false);
        Assert.False(saver.IsEnabled);
        Assert.True(otherNode.IsEnabled);

        await otherNode.RefreshAsync();
        Assert.False(otherNode.IsEnabled);
        Assert.NotNull(otherNode.Current.UpdatedAtUtc);

        await saver.SetAsync(true);
        await otherNode.RefreshAsync();
        Assert.True(otherNode.IsEnabled);

        await using var context = NewContext();
        Assert.Single(context.SystemConfigs, c => c.Key == SapConnectionSwitch.ConfigKey);
    }

    [Fact]
    public async Task Switched_off_the_circuit_reads_open_and_back_on_it_does_not()
    {
        var connectionSwitch = NewSwitch();
        var breaker = NewBreaker(connectionSwitch);

        await connectionSwitch.SetAsync(false);

        Assert.True(breaker.IsSwitchedOff);
        Assert.True(breaker.IsOpen);
        Assert.True(breaker.ShouldShortCircuit(out var retryAfter));
        Assert.Equal(SapCircuitBreakerState.SwitchedOffRetryAfter, retryAfter);
        Assert.True(breaker.GetSnapshot().IsSwitchedOff);
        // The breaker's own record is untouched, so turning SAP back on does not start with it open.
        Assert.False(breaker.GetSnapshot().IsOpen);

        await connectionSwitch.SetAsync(true);

        Assert.False(breaker.IsOpen);
        Assert.False(breaker.ShouldShortCircuit(out _));
    }

    [Fact]
    public async Task Switched_off_no_request_leaves_the_process_and_the_refusal_proves_nothing_was_posted()
    {
        var connectionSwitch = NewSwitch();
        var breaker = NewBreaker(connectionSwitch);
        var sap = new CountingSap();
        using var client = new HttpClient(
            new SAPCircuitBreakerHandler(breaker, NullLogger<SAPCircuitBreakerHandler>.Instance, TimeSpan.FromMinutes(5))
            {
                InnerHandler = sap
            });

        await connectionSwitch.SetAsync(false);

        var ex = await Assert.ThrowsAsync<SapCircuitOpenException>(
            () => client.PostAsync("https://sap.invalid/b1s/v1/Invoices", new StringContent("{}")));

        Assert.Equal(0, sap.Calls);
        Assert.Contains("turned off", ex.Message);
        // Posting services retry a transient failure later and may re-send only what SAP certainly did
        // not create. The refusal has to read as both, or a queued invoice is abandoned or held.
        Assert.True(SapFailureClassifier.IsTransient(ex));
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(ex));
        // Nothing was sent, so nothing counts against the breaker.
        Assert.Equal(0, breaker.GetSnapshot().ConsecutiveFailures);

        await connectionSwitch.SetAsync(true);
        var response = await client.GetAsync("https://sap.invalid/b1s/v1/Items('A')");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, sap.Calls);
    }

    [Fact]
    public async Task Switched_off_the_dependency_check_is_degraded_rather_than_unhealthy()
    {
        var connectionSwitch = NewSwitch();
        await connectionSwitch.SetAsync(false);

        var check = new SapDependencyHealthCheck(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NewBreaker(connectionSwitch));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void The_container_hands_the_breaker_the_switch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => NewContext());
        services.AddSingleton(Options.Create(new SAPSettings { Enabled = true }));
        services.AddSingleton<SapConnectionSwitch>();
        services.AddSingleton<SapCircuitBreakerState>();
        using var provider = services.BuildServiceProvider();

        var connectionSwitch = provider.GetRequiredService<SapConnectionSwitch>();
        var breaker = provider.GetRequiredService<SapCircuitBreakerState>();
        connectionSwitch.SetAsync(false).GetAwaiter().GetResult();

        Assert.True(breaker.IsSwitchedOff);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private SapConnectionSwitch NewSwitch() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SapConnectionSwitch>.Instance);

    private static SapCircuitBreakerState NewBreaker(SapConnectionSwitch connectionSwitch) =>
        new(Options.Create(new SAPSettings { Enabled = true }), connectionSwitch);

    private sealed class CountingSap : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
