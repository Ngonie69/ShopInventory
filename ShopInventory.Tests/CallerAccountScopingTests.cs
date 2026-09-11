using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Security;
using ShopInventory.Controllers;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;
using ShopInventory.Features.Notifications.Queries.GetNotifications;
using ShopInventory.Features.Notifications.Queries.GetUnreadCount;
using ShopInventory.Features.Timesheets.Queries.GetTimesheetReport;
using ShopInventory.Features.Timesheets.Queries.GetTimesheets;
using ShopInventory.Features.UserManagement.Queries.GetUsers;
using ShopInventory.Hubs;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Role questions about a request are answered by the caller's account, not by the role claims on it.
/// </summary>
/// <remarks>
/// ShopInventory.Web sends its integration key and the signed-in user's token on every call. The
/// ApiAccess policies authenticate both, so the principal holds the key's identity — Admin, and first —
/// beside the user's. Every test here builds that merged principal, because no test did before: a
/// merchandiser signed in on the Web read every merchandiser's timesheets while the suite passed.
/// </remarks>
public sealed class CallerAccountScopingTests : IDisposable
{
    private const string WebKeyName = "MainIntegration";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public CallerAccountScopingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ── Timesheets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported bug. The old guard, <c>IsInRole("Merchandiser") &amp;&amp; !IsInRole("Admin")</c>, is
    /// false on this principal because the key's Admin role is on it, so the merchandiser was handed
    /// whatever they asked for — every merchandiser, when the Web page asks for nobody in particular.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_merchandiser_signed_in_on_the_web_reads_only_their_own_timesheets(bool asksForSomeoneElse)
    {
        var merchandiser = await AddUserAsync(ApplicationRoles.Merchandiser);
        var principal = WebRequest(merchandiser, tokenRole: ApplicationRoles.Merchandiser);
        Guid? requested = asksForSomeoneElse ? Guid.NewGuid() : null;

        Assert.True(principal.IsInRole(ApplicationRoles.Admin));
        Assert.False(principal.IsInRole(ApplicationRoles.Merchandiser) && !principal.IsInRole(ApplicationRoles.Admin));

        var sent = new List<object>();
        var controller = Timesheets(sent, principal);

        Assert.IsType<OkObjectResult>(await controller.GetTimesheets(userId: requested));
        Assert.IsType<OkObjectResult>(await controller.GetReport(userId: requested));

        Assert.Equal(merchandiser.Id, Assert.IsType<GetTimesheetsQuery>(sent[0]).UserId);
        Assert.Equal(merchandiser.Id, Assert.IsType<GetTimesheetReportQuery>(sent[1]).UserId);
    }

    /// <summary>
    /// The negative control. A real administrator on the same kind of request is not narrowed; without
    /// this, "every Web user sees only their own rows" would pass the test above as well.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_admin_signed_in_on_the_web_reads_whichever_merchandiser_they_ask_for(bool asksForSomeone)
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        Guid? requested = asksForSomeone ? Guid.NewGuid() : null;
        var sent = new List<object>();
        var controller = Timesheets(sent, WebRequest(admin, tokenRole: ApplicationRoles.Admin));

        Assert.IsType<OkObjectResult>(await controller.GetTimesheets(userId: requested));
        Assert.IsType<OkObjectResult>(await controller.GetReport(userId: requested));

        Assert.Equal(requested, Assert.IsType<GetTimesheetsQuery>(sent[0]).UserId);
        Assert.Equal(requested, Assert.IsType<GetTimesheetReportQuery>(sent[1]).UserId);
    }

    /// <summary>
    /// A token issued before a role change still carries the old role. The account row decides.
    /// </summary>
    [Fact]
    public async Task The_account_role_decides_when_the_token_still_says_admin()
    {
        var demoted = await AddUserAsync(ApplicationRoles.Merchandiser);
        var sent = new List<object>();

        await Timesheets(sent, WebRequest(demoted, tokenRole: ApplicationRoles.Admin)).GetTimesheets();

        Assert.Equal(demoted.Id, Assert.IsType<GetTimesheetsQuery>(sent[0]).UserId);
    }

    [Fact]
    public async Task An_integration_key_on_its_own_is_not_narrowed()
    {
        var sent = new List<object>();
        var requested = Guid.NewGuid();

        await Timesheets(sent, ServiceCall()).GetTimesheets(userId: requested);

        Assert.Equal(requested, Assert.IsType<GetTimesheetsQuery>(sent[0]).UserId);
    }

    /// <summary>
    /// Fail closed. A disabled merchandiser's token still rides beside the Web's key; read as "no
    /// account" it would pass for the service caller and be shown every merchandiser.
    /// </summary>
    [Fact]
    public async Task A_disabled_merchandiser_is_refused_rather_than_shown_everyone()
    {
        var disabled = await AddUserAsync(ApplicationRoles.Merchandiser, isActive: false);
        var controller = new TimesheetController(StubProxy.Unused<IMediator>(), Reader())
        {
            ControllerContext = ContextFor(WebRequest(disabled, ApplicationRoles.Merchandiser))
        };

        var result = Assert.IsType<ObjectResult>(await controller.GetTimesheets());

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    // ── The other role questions ─────────────────────────────────────────────────

    [Theory]
    [InlineData(ApplicationRoles.Driver, true)]
    [InlineData(ApplicationRoles.Operator, true)]
    [InlineData(ApplicationRoles.Admin, false)]
    public async Task An_invoice_looked_up_on_the_web_is_held_to_the_callers_assigned_customers_by_role(string role, bool restricted)
    {
        var account = await AddUserAsync(role);
        var sent = new List<object>();
        var controller = new InvoiceController(Recording(sent), Reader()) { ControllerContext = ContextFor(WebRequest(account, role)) };

        await controller.GetInvoiceByDocNum(1001);

        var query = Assert.IsType<GetInvoiceByDocNumQuery>(sent[0]);
        Assert.Equal(restricted, query.RestrictToAssignedCustomers);
        Assert.Equal(restricted ? account.Id : (Guid?)null, query.RequestingUserId);
    }

    /// <summary>
    /// Read off the request, this Web user was the key: its name is the first Name claim and its Admin
    /// role sat beside theirs, so the bell showed the Admin view and nothing addressed to them.
    /// </summary>
    [Fact]
    public async Task A_web_users_notifications_are_read_for_their_own_account()
    {
        var merchandiser = await AddUserAsync(ApplicationRoles.Merchandiser);
        var principal = WebRequest(merchandiser, ApplicationRoles.Merchandiser);
        Assert.Equal(WebKeyName, principal.FindFirst(ClaimTypes.Name)?.Value);

        var sent = new List<object>();
        var controller = new NotificationController(Recording(sent), Reader()) { ControllerContext = ContextFor(principal) };

        await controller.GetNotifications();

        var query = Assert.IsType<GetNotificationsQuery>(sent[0]);
        Assert.Equal(merchandiser.Username, query.Username);
        Assert.Equal([ApplicationRoles.Merchandiser], query.Roles);
    }

    [Fact]
    public async Task An_integration_key_on_its_own_reads_notifications_as_the_key()
    {
        var sent = new List<object>();
        var controller = new NotificationController(Recording(sent), Reader()) { ControllerContext = ContextFor(ServiceCall()) };

        await controller.GetUnreadCount(CancellationToken.None);

        var query = Assert.IsType<GetUnreadCountQuery>(sent[0]);
        Assert.Equal(WebKeyName, query.Username);
        Assert.Equal([ApplicationRoles.Admin, ApplicationRoles.ApiUser], query.Roles);
    }

    [Fact]
    public async Task A_hub_connection_carrying_the_key_joins_the_users_groups_not_the_keys()
    {
        var cashier = await AddUserAsync(ApplicationRoles.Cashier);
        var groups = new RecordingGroupManager();
        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance, Reader())
        {
            Context = new StubHubCallerContext(WebRequest(cashier, ApplicationRoles.Cashier)),
            Groups = groups
        };

        await hub.OnConnectedAsync();

        Assert.Contains($"user:{cashier.Username}", groups.Joined);
        Assert.Contains($"role:{ApplicationRoles.Cashier}", groups.Joined);
        Assert.DoesNotContain($"role:{ApplicationRoles.Admin}", groups.Joined);
        Assert.DoesNotContain($"user:{WebKeyName}", groups.Joined);
    }

    [Theory]
    [InlineData(ApplicationRoles.PodOperator, true)]
    [InlineData(ApplicationRoles.Admin, false)]
    public async Task A_pod_operator_on_the_web_lists_only_drivers(string role, bool driversOnly)
    {
        var caller = await AddUserAsync(role);
        var driver = await AddUserAsync(ApplicationRoles.Driver);
        var cashier = await AddUserAsync(ApplicationRoles.Cashier);

        await using var context = new ApplicationDbContext(_options);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = WebRequest(caller, role) } };
        var result = await new GetUsersHandler(context, accessor, Reader()).Handle(new GetUsersQuery(PageSize: 100), CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        var listed = result.Value.Items.Select(user => user.Id).ToList();
        Assert.Contains(driver.Id, listed);
        Assert.Equal(!driversOnly, listed.Contains(cashier.Id));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the API sees from the Web: the key's identity as <c>ApiKeyAuthenticationHandler</c> issues
    /// it, then the user's token. The policy evaluator puts the later scheme's identity first.
    /// </summary>
    private static ClaimsPrincipal WebRequest(User account, string tokenRole) => new(
    [
        KeyIdentity(),
        new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Name, account.Username),
                new Claim(ClaimTypes.Role, tokenRole)
            ],
            "Bearer")
    ]);

    private static ClaimsPrincipal ServiceCall() => new(KeyIdentity());

    private static ClaimsIdentity KeyIdentity() => new(
        [
            new Claim(ClaimTypes.Name, WebKeyName),
            new Claim(ClaimTypes.AuthenticationMethod, "ApiKey"),
            new Claim("api_key_name", WebKeyName),
            new Claim(ClaimTypes.Role, ApplicationRoles.Admin),
            new Claim(ClaimTypes.Role, ApplicationRoles.ApiUser)
        ],
        "ApiKey");

    private ICallerAccountReader Reader() => new CallerAccountReader(new ApplicationDbContext(_options));

    private static ControllerContext ContextFor(ClaimsPrincipal principal) =>
        new() { HttpContext = new DefaultHttpContext { User = principal } };

    private TimesheetController Timesheets(List<object> sent, ClaimsPrincipal principal) =>
        new(Recording(sent), Reader()) { ControllerContext = ContextFor(principal) };

    /// <summary>Remembers each request and answers it with an empty result of the right shape.</summary>
    private static IMediator Recording(List<object> sent) =>
        StubProxy.For<IMediator>((method, args) =>
        {
            if (method.Name != nameof(IMediator.Send))
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            var request = args![0]!;
            sent.Add(request);

            return request switch
            {
                GetTimesheetsQuery query => (object)Task.FromResult<ErrorOr<TimesheetListResult>>(new TimesheetListResult([], 0, query.Page, query.PageSize)),
                GetTimesheetReportQuery query => Task.FromResult<ErrorOr<TimesheetReportResult>>(new TimesheetReportResult(query.FromDate, query.ToDate, [], 0, 0, 0)),
                GetNotificationsQuery => Task.FromResult<ErrorOr<NotificationListResponseDto>>(new NotificationListResponseDto()),
                GetUnreadCountQuery => Task.FromResult<ErrorOr<int>>(0),
                GetInvoiceByDocNumQuery => Task.FromResult<ErrorOr<InvoiceDto>>(new InvoiceDto()),
                _ => throw new InvalidOperationException($"No answer for {request.GetType().Name}")
            };
        });

    private async Task<User> AddUserAsync(string role, bool isActive = true)
    {
        await using var context = new ApplicationDbContext(_options);
        var user = new User
        {
            Username = $"user-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            Role = role,
            IsActive = isActive
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<string> Joined { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
