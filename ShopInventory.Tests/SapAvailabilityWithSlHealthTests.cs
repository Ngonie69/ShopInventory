using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Sync.Queries.GetSapAvailability;
using ShopInventory.Health;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What reading SL Health changes about how the API judges and describes SAP, and the proof that
/// without an answer fit to act on it changes nothing at all.
/// </summary>
/// <remarks>
/// The probe, the banner's query and the health check are the real ones, over the SL Health reader as
/// <c>Program.cs</c> registers it. Each test that needs "exactly as today" asserts the whole answer,
/// not the one field SL Health would have touched.
/// </remarks>
public sealed class SapAvailabilityWithSlHealthTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly ApplicationDbContext _context;
    private readonly SapAvailability _availability;
    private readonly SapConnectionSwitch _switch;
    private readonly SapCircuitBreakerState _circuit;
    private bool _sapAnswers = true;

    public SapAvailabilityWithSlHealthTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection))
            .AddScoped(_ => StubProxy.For<ISyncStatusService>((method, _) => method.Name switch
            {
                nameof(ISyncStatusService.CheckSapConnectionAsync) => Task.FromResult(new SapConnectionStatusDto
                {
                    IsConnected = _sapAnswers,
                    CompanyDb = "KEFALOS_TEST",
                    ResponseTimeMs = 41,
                    LastError = _sapAnswers ? null : "Connection refused"
                }),
                _ => throw new InvalidOperationException($"{method.Name} was not expected.")
            }))
            .BuildServiceProvider();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        _availability = new SapAvailability(scopes, NullLogger<SapAvailability>.Instance);
        _switch = new SapConnectionSwitch(scopes, NullLogger<SapConnectionSwitch>.Instance);
        _circuit = new SapCircuitBreakerState(Options.Create(new SAPSettings { Enabled = true }), _switch, _availability);
    }

    public void Dispose()
    {
        _context.Dispose();
        _services.Dispose();
        _connection.Dispose();
    }

    // ---- Saying why ---------------------------------------------------------------------------

    [Fact]
    public async Task The_outage_row_carries_what_sl_health_said_when_it_was_declared()
    {
        using var slHealth = new SlHealthHarness();
        _sapAnswers = false;
        var maintenance = SlHealthJson.Status(
            level: "Down", advice: "Unavailable", pingOk: false,
            maintenanceReason: "HANA backup", maintenanceUntil: slHealth.Clock.GetUtcNow().AddHours(1),
            reasons: ["Service Layer is not answering /ping"]);

        var run = SapProbeRun.None;
        for (var probes = 0; probes < 3; probes++)
        {
            run = await ProbeAsync(slHealth, run, hears: maintenance);
        }

        var outage = Assert.Single(await Outages());
        Assert.Equal(SapOutageCauses.Unreachable, outage.Cause);
        Assert.Equal("planned maintenance until 03:00 (HANA backup)", outage.Explanation);
    }

    [Fact]
    public async Task The_outage_row_keeps_the_latest_thing_sl_health_had_to_say()
    {
        using var slHealth = new SlHealthHarness();
        _sapAnswers = false;
        var down = SlHealthJson.Status(level: "Down", advice: "Unavailable", pingOk: false, reasons: ["Service Layer is not answering /ping"]);

        var run = SapProbeRun.None;
        for (var probes = 0; probes < 3; probes++)
        {
            run = await ProbeAsync(slHealth, run, hears: down);
        }

        Assert.Equal("DOWN: Service Layer is not answering /ping", Assert.Single(await Outages()).Explanation);

        run = await ProbeAsync(slHealth, run, hears: SlHealthJson.Status(level: "Down", advice: "Unavailable", pingOk: false, restartTarget: "ServiceLayer"));
        Assert.Equal("SAP is being restarted", Assert.Single(await Outages()).Explanation);

        // SL Health goes quiet for the rest of the outage: what it last said stays on the row.
        slHealth.Transport.Respond = (_, _) => throw new HttpRequestException("No connection could be made.");
        slHealth.Clock.Advance(TimeSpan.FromMinutes(5));
        await slHealth.Monitor.RefreshAsync();
        await ProbeAsync(slHealth, run);

        var outage = Assert.Single(await Outages());
        Assert.Equal("SAP is being restarted", outage.Explanation);
        Assert.Equal(5, outage.FailedProbes);
    }

    [Fact]
    public async Task Switching_sap_off_inside_a_planned_window_records_the_window()
    {
        using var slHealth = new SlHealthHarness();
        await _switch.SetAsync(false);

        await ProbeAsync(slHealth, SapProbeRun.None, hears: SlHealthJson.Status(
            maintenanceReason: "Company database move", maintenanceUntil: slHealth.Clock.GetUtcNow().AddHours(1)));

        var outage = Assert.Single(await Outages());
        Assert.Equal(SapOutageCauses.SwitchedOff, outage.Cause);
        Assert.Equal("planned maintenance until 03:00 (Company database move)", outage.Explanation);
    }

    [Fact]
    public async Task The_banner_is_told_why_while_sap_is_down_and_not_once_it_is_back()
    {
        using var slHealth = new SlHealthHarness();
        var since = DateTime.UtcNow.AddMinutes(-20);
        _availability.Apply(new SapAvailabilityState(7, since, SapOutageCauses.Unreachable));
        await slHealth.HasHeardAsync(SlHealthJson.Status(level: "Down", advice: "Unavailable", pingOk: false, restartTarget: "ServiceLayer"));

        var down = await AskAsync(slHealth);

        Assert.Equal(new SapAvailabilityResult(true, SapOutageCauses.Unreachable, since, null, 0, "SAP is being restarted"), down);

        _availability.Apply(SapAvailabilityState.Available);
        _context.SapOutages.Add(new SapOutageEntity
        {
            StartedAtUtc = since,
            DeclaredAtUtc = since.AddMinutes(1),
            EndedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            Cause = SapOutageCauses.Unreachable,
            LastProbeAtUtc = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var back = await AskAsync(slHealth);

        Assert.False(back.IsDown);
        Assert.NotNull(back.EndedAtUtc);
        Assert.Null(back.Explanation);
    }

    [Fact]
    public async Task The_banner_is_told_nothing_extra_when_sl_health_finds_sap_well()
    {
        using var slHealth = new SlHealthHarness();
        var since = DateTime.UtcNow.AddMinutes(-20);
        _availability.Apply(new SapAvailabilityState(7, since, SapOutageCauses.Unreachable));
        await slHealth.HasHeardAsync(SlHealthJson.Status());

        Assert.Equal(new SapAvailabilityResult(true, SapOutageCauses.Unreachable, since, null, 0), await AskAsync(slHealth));
    }

    [Fact]
    public async Task The_sap_health_check_carries_sl_healths_level_and_reasons_without_changing_its_status()
    {
        using var slHealth = new SlHealthHarness();
        await slHealth.HasHeardAsync(SlHealthJson.Status(
            level: "Critical", advice: "HoldBackground", serverToolsOk: false,
            reasons: ["Server Tools (SLD, licensing) not answering: HTTP 503. New logins will fail"]));

        var healthy = await CheckAsync(slHealth);

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal("SAP dependency is reachable.", healthy.Description);
        Assert.Equal("KEFALOS_TEST", healthy.Data["companyDb"]);
        Assert.Equal("Critical", healthy.Data["slHealthLevel"]);
        Assert.Equal("HoldBackground", healthy.Data["slHealthAdvice"]);
        Assert.Equal(
            new[] { "Server Tools (SLD, licensing) not answering: HTTP 503. New logins will fail" },
            Assert.IsType<string[]>(healthy.Data["slHealthReasons"]));
        Assert.Equal("new logins will fail: Server Tools not answering", healthy.Data["slHealthExplanation"]);

        _availability.Apply(new SapAvailabilityState(7, DateTime.UtcNow, SapOutageCauses.Unreachable));
        await slHealth.HasHeardAsync(SlHealthJson.Status(level: "Healthy", advice: "Proceed"));

        var outage = await CheckAsync(slHealth);

        Assert.Equal(HealthStatus.Unhealthy, outage.Status);
        Assert.Equal("SAP is unavailable (declared outage).", outage.Description);
        Assert.Equal(7, outage.Data["outageId"]);
        Assert.Equal("Healthy", outage.Data["slHealthLevel"]);
        Assert.False(outage.Data.ContainsKey("slHealthExplanation"));
    }

    // ---- Exactly as today when there is no answer ----------------------------------------------

    [Theory]
    [MemberData(nameof(NoAnswerCases))]
    public async Task With_no_answer_an_outage_is_declared_on_the_third_failure_with_nothing_added(string noAnswerCase)
    {
        using var slHealth = await SlHealthHarness.WithNoAnswerAsync(noAnswerCase);
        _sapAnswers = false;

        var run = await ProbeAsync(slHealth, SapProbeRun.None);
        var firstFailure = slHealth.Clock.GetUtcNow().UtcDateTime.AddSeconds(-30);
        Assert.Empty(await Outages());

        run = await ProbeAsync(slHealth, run);
        Assert.Empty(await Outages());
        Assert.False(_availability.IsInOutage);

        run = await ProbeAsync(slHealth, run);

        var outage = Assert.Single(await Outages());
        Assert.Equal(firstFailure, outage.StartedAtUtc);
        Assert.Equal(firstFailure.AddSeconds(60), outage.DeclaredAtUtc);
        Assert.Equal(SapOutageCauses.Unreachable, outage.Cause);
        Assert.Equal(3, outage.FailedProbes);
        Assert.Null(outage.Explanation);
        Assert.Equal(3, run.Failures);
    }

    [Theory]
    [MemberData(nameof(NoAnswerCases))]
    public async Task With_no_answer_the_banner_and_the_health_check_say_what_they_said_before(string noAnswerCase)
    {
        using var slHealth = await SlHealthHarness.WithNoAnswerAsync(noAnswerCase);

        Assert.Equal(SapAvailabilityResult.Available, await AskAsync(slHealth));

        var healthy = await CheckAsync(slHealth);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(new[] { "companyDb", "consecutiveFailures", "responseTimeMs" }, healthy.Data.Keys.Order());

        var since = DateTime.UtcNow.AddMinutes(-20);
        _availability.Apply(new SapAvailabilityState(7, since, SapOutageCauses.Unreachable));

        Assert.Equal(new SapAvailabilityResult(true, SapOutageCauses.Unreachable, since, null, 0), await AskAsync(slHealth));

        var outage = await CheckAsync(slHealth);
        Assert.Equal(HealthStatus.Unhealthy, outage.Status);
        Assert.Equal("SAP is unavailable (declared outage).", outage.Description);
        Assert.Equal(new[] { "cause", "outageId", "sinceUtc" }, outage.Data.Keys.Order());
    }

    public static TheoryData<string> NoAnswerCases() => new(SlHealthHarness.NoAnswerCases);

    // ---- Bench ----------------------------------------------------------------------------------

    /// <summary>One firing of the probe job, thirty seconds before the next, as the refresher would have left SL Health.</summary>
    private async Task<SapProbeRun> ProbeAsync(SlHealthHarness slHealth, SapProbeRun run, string? hears = null)
    {
        if (hears is not null)
        {
            await slHealth.HasHeardAsync(hears);
        }

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.PingAsync) => _sapAnswers
                ? Task.CompletedTask
                : Task.FromException(new HttpRequestException("Connection refused")),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        await using var scope = _services.CreateAsyncScope();
        var probe = new SapAvailabilityProbe(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            sap,
            _circuit,
            _availability,
            slHealth.Monitor,
            Options.Create(new SapAvailabilitySettings()),
            slHealth.Clock,
            NullLogger<SapAvailabilityProbe>.Instance);

        var next = await probe.RunAsync(run, CancellationToken.None);
        slHealth.Clock.Advance(TimeSpan.FromSeconds(30));
        return next;
    }

    private async Task<SapAvailabilityResult> AskAsync(SlHealthHarness slHealth)
    {
        var result = await new GetSapAvailabilityHandler(_context, _availability, slHealth.Monitor)
            .Handle(new GetSapAvailabilityQuery(), default);
        Assert.False(result.IsError);
        return result.Value;
    }

    private Task<HealthCheckResult> CheckAsync(SlHealthHarness slHealth) =>
        new SapDependencyHealthCheck(_services.GetRequiredService<IServiceScopeFactory>(), _circuit, _availability, slHealth.Monitor)
            .CheckHealthAsync(new HealthCheckContext());

    private Task<List<SapOutageEntity>> Outages() =>
        _context.SapOutages.AsNoTracking().OrderBy(outage => outage.Id).ToListAsync();
}
