using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The recent-activity card on /security reads the signed-in account's own history.
/// </summary>
/// <remarks>
/// It used to read api/useractivity?username=..., which is the whole audit log narrowed by a name
/// the caller chooses, guarded by audit.view. Only Admin holds that. The Web's API key used to carry
/// every request past the check, so the card worked for everyone. Now the API checks the signed-in
/// user, so every other role would be refused, and the Web swallows that failure: the card would
/// quietly lose its API rows. api/useractivity/me takes the user from the token and needs no
/// permission.
/// </remarks>
public sealed class SecurityPageOwnActivityTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 11, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WebAppDbContext> _options;

    public SecurityPageOwnActivityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<WebAppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new WebAppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Reads_the_callers_own_activity_not_the_audit_log()
    {
        await SeedLocalAsync(
            Local("verify-cashier", "Login", Now.AddHours(-2)),
            Local("verify-cashier", "ViewInvoice", Now.AddHours(-1)),
            Local("someone-else", "Login", Now.AddMinutes(-30)));

        var api = new RecordingHandler(HttpStatusCode.OK, MeResponse(
            actionsToday: 3,
            actionsThisWeek: 41,
            Api(9001, "verify-cashier", "CreateInvoice", Now)));

        var result = await Service(api).GetMyRecentActivityAsync("verify-cashier", 10);

        var request = Assert.Single(api.Requests);
        Assert.Equal("/api/useractivity/me", request.AbsolutePath);
        Assert.Equal("?recentCount=10", request.Query);

        Assert.Equal(3, result.ActionsToday);
        Assert.Equal(41, result.ActionsThisWeek);
        Assert.Equal(["CreateInvoice", "ViewInvoice", "Login"], result.Items.Select(item => item.Action));
        Assert.All(result.Items, item => Assert.Equal("verify-cashier", item.Username));
    }

    /// <summary>
    /// The Web and the API can both record one action. The merge keeps the existing five-second
    /// de-duplication, so the card does not show it twice.
    /// </summary>
    [Fact]
    public async Task An_action_recorded_by_both_apps_appears_once()
    {
        await SeedLocalAsync(Local("verify-cashier", "CancelInvoice", Now, entityType: "Invoice", entityId: "42"));

        var api = new RecordingHandler(HttpStatusCode.OK, MeResponse(
            actionsToday: 1,
            actionsThisWeek: 1,
            Api(9002, "verify-cashier", "CancelInvoice", Now.AddSeconds(2), entityType: "Invoice", entityId: "42")));

        var result = await Service(api).GetMyRecentActivityAsync("verify-cashier", 10);

        Assert.Single(result.Items);
    }

    /// <summary>
    /// The negative control. When the API refuses or fails, this app's rows still show. The counts
    /// are unknown rather than zero, so the page can fall back to counting what it has.
    /// </summary>
    [Fact]
    public async Task When_the_api_cannot_answer_the_local_rows_still_show()
    {
        await SeedLocalAsync(Local("verify-cashier", "Login", Now));

        var api = new RecordingHandler(HttpStatusCode.Forbidden, "{}");

        var result = await Service(api).GetMyRecentActivityAsync("verify-cashier", 10);

        Assert.Null(result.ActionsToday);
        Assert.Null(result.ActionsThisWeek);
        Assert.Equal(["Login"], result.Items.Select(item => item.Action));
    }

    private AuditService Service(RecordingHandler handler) =>
        new(
            new TestDbContextFactory(_options),
            new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") },
            NullLogger<AuditService>.Instance,
            new SignedOutAuthenticationStateProvider(),
            new WebClientAuditContext());

    private async Task SeedLocalAsync(params AuditLog[] logs)
    {
        await using var context = new WebAppDbContext(_options);
        context.AuditLogs.AddRange(logs);
        await context.SaveChangesAsync();
    }

    private static AuditLog Local(string username, string action, DateTime timestamp, string? entityType = null, string? entityId = null) => new()
    {
        Username = username,
        UserRole = "Cashier",
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        IsSuccess = true,
        Timestamp = timestamp
    };

    private static object Api(int id, string username, string action, DateTime timestamp, string? entityType = null, string? entityId = null) => new
    {
        id,
        username,
        userRole = "Cashier",
        action,
        entityType,
        entityId,
        isSuccess = true,
        timestamp
    };

    private static string MeResponse(int actionsToday, int actionsThisWeek, params object[] recentActivities) =>
        JsonSerializer.Serialize(new
        {
            userId = Guid.NewGuid(),
            username = "verify-cashier",
            totalActions = 120,
            actionsToday,
            actionsThisWeek,
            recentActivities
        });

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WebAppDbContext> options) : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }

    private sealed class SignedOutAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
