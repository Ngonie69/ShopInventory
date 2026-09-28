using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins when the cluster declares SAP down and when it declares it back, and what the posting passes
/// see while it is down.
/// </summary>
public sealed class SapAvailabilityProbeTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly ApplicationDbContext _context;
    private readonly SteppingClock _clock = new(Start);
    private readonly SapAvailability _availability;
    private readonly SapConnectionSwitch _switch;
    private readonly SapCircuitBreakerState _circuit;
    private Func<CancellationToken, Task> _ping = _ => Task.CompletedTask;
    private int _pings;

    public SapAvailabilityProbeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        _availability = new SapAvailability(scopes, NullLogger<SapAvailability>.Instance);
        _switch = new SapConnectionSwitch(scopes, NullLogger<SapConnectionSwitch>.Instance);
        _circuit = new SapCircuitBreakerState(Options.Create(new SAPSettings()), _switch, _availability);
    }

    public void Dispose()
    {
        _context.Dispose();
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_outage_is_declared_on_the_third_failure_in_a_row_and_dated_from_the_first()
    {
        _ping = _ => throw new HttpRequestException("Connection refused");

        var run = SapProbeRun.None;
        run = await ProbeAsync(run);
        run = await ProbeAsync(run);

        Assert.Empty(await Outages());
        Assert.False(_availability.IsInOutage);

        var firstFailure = Start;
        run = await ProbeAsync(run);

        var outage = Assert.Single(await Outages());
        Assert.Equal(firstFailure, outage.StartedAtUtc);
        Assert.Equal(Start.AddSeconds(60), outage.DeclaredAtUtc);
        Assert.Null(outage.EndedAtUtc);
        Assert.Equal(SapOutageCauses.Unreachable, outage.Cause);
        Assert.Equal(3, outage.FailedProbes);
        Assert.Contains("Connection refused", outage.FirstError);
        Assert.True(_availability.IsInOutage);
        Assert.Equal(3, run.Failures);
    }

    [Fact]
    public async Task A_single_answer_between_failures_starts_the_count_again()
    {
        var results = new Queue<bool>([false, false, true, false, false]);
        _ping = _ => results.Dequeue() ? Task.CompletedTask : throw new HttpRequestException("reset");

        var run = SapProbeRun.None;
        for (var i = 0; i < 5; i++)
        {
            run = await ProbeAsync(run);
        }

        Assert.Empty(await Outages());
        Assert.Equal(2, run.Failures);
    }

    [Fact]
    public async Task An_outage_ends_on_the_second_answer_in_a_row_and_is_dated_to_the_first()
    {
        _ping = _ => throw new HttpRequestException("Connection refused");
        var run = SapProbeRun.None;
        for (var i = 0; i < 4; i++)
        {
            run = await ProbeAsync(run);
        }

        _ping = _ => Task.CompletedTask;
        var firstAnswer = _clock.GetUtcNow().UtcDateTime;
        run = await ProbeAsync(run);

        Assert.Null(Assert.Single(await Outages()).EndedAtUtc);
        Assert.True(_availability.IsInOutage);

        run = await ProbeAsync(run);

        var outage = Assert.Single(await Outages());
        Assert.Equal(firstAnswer, outage.EndedAtUtc);
        Assert.Equal(4, outage.FailedProbes);
        Assert.False(_availability.IsInOutage);
        Assert.Equal(2, run.Successes);
    }

    [Fact]
    public async Task A_probe_that_gets_no_answer_in_time_is_a_failure()
    {
        _ping = token => Task.Delay(Timeout.Infinite, token);

        var run = await ProbeAsync(SapProbeRun.None, new SapAvailabilitySettings { ProbeTimeoutSeconds = 1 });

        Assert.Equal(1, run.Failures);
        Assert.Contains("did not answer", run.FirstError);
    }

    [Fact]
    public async Task Turning_sap_off_opens_an_outage_at_once_and_turning_it_on_waits_for_an_answer()
    {
        await _switch.SetAsync(false);
        _ping = _ => throw new InvalidOperationException("Nothing may be sent while SAP is switched off.");

        var run = await ProbeAsync(SapProbeRun.None);

        var outage = Assert.Single(await Outages());
        Assert.Equal(SapOutageCauses.SwitchedOff, outage.Cause);
        Assert.Null(outage.EndedAtUtc);
        Assert.Equal(0, _pings);
        Assert.Equal(SapProbeRun.None, run);

        await _switch.SetAsync(true);
        _ping = _ => Task.CompletedTask;

        run = await ProbeAsync(run);
        Assert.Null(Assert.Single(await Outages()).EndedAtUtc);

        await ProbeAsync(run);
        Assert.NotNull(Assert.Single(await Outages()).EndedAtUtc);
    }

    [Fact]
    public async Task Background_work_holds_back_during_an_outage_though_this_nodes_circuit_is_closed()
    {
        _ping = _ => throw new HttpRequestException("Connection refused");
        var run = SapProbeRun.None;
        for (var i = 0; i < 3; i++)
        {
            run = await ProbeAsync(run);
        }

        Assert.False(_circuit.ShouldShortCircuit(out _));
        Assert.True(_circuit.ShouldHoldBackWork(out var reason));
        Assert.Contains("unavailable since", reason);
    }

    [Fact]
    public async Task Another_node_learns_of_the_outage_from_the_database()
    {
        _ping = _ => throw new HttpRequestException("Connection refused");
        var run = SapProbeRun.None;
        for (var i = 0; i < 3; i++)
        {
            run = await ProbeAsync(run);
        }

        var otherNode = new SapAvailability(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SapAvailability>.Instance);
        Assert.False(otherNode.IsInOutage);

        await otherNode.RefreshAsync();

        Assert.True(otherNode.IsInOutage);
        Assert.Equal(Start, otherNode.Current.SinceUtc);
    }

    [Fact]
    public void The_run_survives_the_trip_through_the_job_data_map()
    {
        var run = new SapProbeRun(2, 0, Start, "Connection refused");
        var data = new JobDataMap();

        SapAvailabilityProbeJob.Write(data, run);

        Assert.Equal(run, SapAvailabilityProbeJob.Read(data));
        Assert.Equal(SapProbeRun.None, SapAvailabilityProbeJob.Read(new JobDataMap()));
    }

    private async Task<SapProbeRun> ProbeAsync(SapProbeRun run, SapAvailabilitySettings? settings = null)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.PingAsync) => Ping((CancellationToken)args![0]!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        // A fresh context per probe, as each job firing gets its own scope.
        await using var scope = _services.CreateAsyncScope();
        var probe = new SapAvailabilityProbe(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            sap,
            _circuit,
            _availability,
            Options.Create(settings ?? new SapAvailabilitySettings()),
            _clock,
            NullLogger<SapAvailabilityProbe>.Instance);

        var next = await probe.RunAsync(run, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        return next;
    }

    private Task Ping(CancellationToken token)
    {
        _pings++;
        return _ping(token);
    }

    private Task<List<SapOutageEntity>> Outages() =>
        _context.SapOutages.AsNoTracking().OrderBy(o => o.Id).ToListAsync();

    private sealed class SteppingClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
