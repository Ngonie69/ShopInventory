using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Which SAP stages of the credit memo approval queue an account may see and decide. The app decides every
/// request as one SAP service approver, so SAP cannot tell the wash bay from a manager — this is the
/// only thing that does.
/// </summary>
public sealed class CreditNoteApprovalStageScopeTests : IDisposable
{
    private static readonly SAPUser Manager = new() { InternalKey = 1, UserCode = "manager", UserName = "Site Manager" };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public CreditNoteApprovalStageScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ── By account ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The bug the first live run found. The Web sends its integration key with the user's token, so the
    /// request's role claims carry the key's Admin as well as WashBay. Scoped by account, the wash bay's
    /// own role decides, whatever else the request says.
    /// </summary>
    [Fact]
    public async Task The_scope_follows_the_account_role_not_the_roles_on_the_request()
    {
        var washBay = await AddUserAsync(ApplicationRoles.WashBay);

        var result = await Scope(WashBayConfigured(), WashBayAndByoStages()).ResolveAsync(washBay.Id, CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.Equal([4], result.Value.StageCodes!);
    }

    /// <summary>
    /// What the controller hands the scope from such a request: the user's id, found beside the key's
    /// identity. The role claim it used to read here was the key's.
    /// </summary>
    [Fact]
    public void A_request_carrying_the_integration_key_and_a_users_token_still_names_the_user()
    {
        var userId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "web"), new Claim(ClaimTypes.AuthenticationMethod, "ApiKey"), new Claim(ClaimTypes.Role, ApplicationRoles.Admin)], "ApiKey"),
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, ApplicationRoles.WashBay)], "Bearer")
        ]);

        Assert.Equal(ApplicationRoles.Admin, principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal(userId, UserClaimReader.GetUserId(principal));
    }

    [Fact]
    public async Task A_service_caller_with_no_account_sees_every_stage()
    {
        var result = await Scope(WashBayConfigured(), WashBayAndByoStages()).ResolveAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Null(result.Value.StageCodes);
    }

    [Fact]
    public async Task An_unknown_or_disabled_account_is_refused_rather_than_seeing_every_stage()
    {
        var disabled = await AddUserAsync(ApplicationRoles.WashBay, isActive: false);
        var scope = Scope(WashBayConfigured(), WashBayAndByoStages());

        var unknown = await scope.ResolveAsync(Guid.NewGuid(), CancellationToken.None);
        var inactive = await scope.ResolveAsync(disabled.Id, CancellationToken.None);

        Assert.True(unknown.IsError);
        Assert.StartsWith("Auth.", unknown.FirstError.Code);
        Assert.True(inactive.IsError);
    }

    // ── By role ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_role_with_no_scope_configured_sees_every_stage_without_asking_sap()
    {
        var lookups = WashBayAndByoStages();
        lookups.Unavailable = true;

        var result = await Scope(new CreditNoteApprovalSettings(), lookups).ResolveForRoleAsync(ApplicationRoles.Manager, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Null(result.Value.StageCodes);
        Assert.True(result.Value.Admits(5));
    }

    [Fact]
    public async Task The_wash_bay_resolves_its_stage_names_to_the_codes_sap_gives_them()
    {
        var settings = new CreditNoteApprovalSettings
        {
            RoleStageScopes = { ["washbay"] = [" Wash Bay Approvals "] }
        };

        var result = await Scope(settings, WashBayAndByoStages()).ResolveForRoleAsync("WashBay", CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.Equal([4], result.Value.StageCodes!);
        Assert.True(result.Value.Admits(4));
        Assert.False(result.Value.Admits(5));
        Assert.False(result.Value.Admits(null));
        Assert.Equal("'Wash Bay Approvals'", result.Value.Describe());
    }

    /// <summary>
    /// Fail closed. A deployment whose settings lost the entry must not hand the wash bay every credit
    /// memo in the company.
    /// </summary>
    [Fact]
    public async Task The_wash_bay_with_no_stage_configured_is_refused_rather_than_shown_everything()
    {
        var result = await Scope(new CreditNoteApprovalSettings(), WashBayAndByoStages()).ResolveForRoleAsync("WashBay", CancellationToken.None);

        Assert.Equal("CreditNoteApproval.StageScopeNotConfigured", result.FirstError.Code);
    }

    [Fact]
    public async Task A_stage_name_sap_does_not_have_is_an_error_not_an_empty_queue()
    {
        var settings = new CreditNoteApprovalSettings { RoleStageScopes = { ["WashBay"] = ["Wash Bay"] } };

        var result = await Scope(settings, WashBayAndByoStages()).ResolveForRoleAsync("WashBay", CancellationToken.None);

        Assert.Equal("CreditNoteApproval.StageScopeUnresolved", result.FirstError.Code);
        Assert.Contains("'Wash Bay'", result.FirstError.Description);
    }

    [Fact]
    public async Task Sap_not_answering_is_reported_as_unavailable()
    {
        var lookups = WashBayAndByoStages();
        lookups.Unavailable = true;

        var result = await Scope(WashBayConfigured(), lookups).ResolveForRoleAsync("WashBay", CancellationToken.None);

        Assert.Equal("CreditNoteApproval.SapUnavailable", result.FirstError.Code);
    }

    /// <summary>
    /// The shipped setting, read the way the host reads it. Without the entry the wash bay is refused
    /// the whole page in production, which a unit test over hand-built settings cannot see.
    /// </summary>
    [Fact]
    public void The_shipped_settings_scope_the_wash_bay_to_the_production_washbay_stage()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(ApiAppSettingsPath(), optional: false).Build();
        var settings = new CreditNoteApprovalSettings();
        configuration.GetSection(CreditNoteApprovalSettings.SectionName).Bind(settings);

        Assert.Equal(["Wash Bay Approvals"], CreditNoteApprovalStageScope.ConfiguredStageNames(settings, ApplicationRoles.WashBay));
        Assert.Empty(CreditNoteApprovalStageScope.ConfiguredStageNames(settings, ApplicationRoles.Manager));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private CreditNoteApprovalStageScope Scope(CreditNoteApprovalSettings settings, FakeSapApprovalLookups lookups)
        => new(new ApplicationDbContext(_options), Options.Create(settings), lookups, NullLogger<CreditNoteApprovalStageScope>.Instance);

    private static CreditNoteApprovalSettings WashBayConfigured()
        => new() { RoleStageScopes = { ["WashBay"] = ["Wash Bay Approvals"] } };

    private static FakeSapApprovalLookups WashBayAndByoStages()
    {
        var lookups = FakeSapApprovalLookups.WithStage(4, "Wash Bay Approvals", [Manager], 1);
        lookups.Stages[5] = new SAPApprovalStage { Code = 5, Name = "BYO", NoOfApproversRequired = 1, ApprovalStageApprovers = [] };
        return lookups;
    }

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

    private static string ApiAppSettingsPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ShopInventory", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("ShopInventory/appsettings.json was not found above the test output folder.");
    }
}
