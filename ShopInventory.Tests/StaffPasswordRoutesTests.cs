using System.IdentityModel.Tokens.Jwt;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Commands.Register;
using ShopInventory.Features.Password;
using ShopInventory.Features.Users.Commands.AdminChangePassword;
using ShopInventory.Features.Users.Commands.RequirePasswordChange;
using ShopInventory.Features.VanSalesCompatibility.Commands.ChangeVanSalesPassword;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the password policy on every route that sets a staff password, and what sign-in says
/// about an account afterwards.
/// </summary>
/// <remarks>
/// The policy itself is proved in <see cref="PasswordPolicyTests"/> and
/// <see cref="UserPasswordsTests"/>. What is proved here is that each route actually goes through
/// it — which is the thing that was not true before, when the route decided the rules — and
/// <see cref="No_other_code_puts_a_password_on_a_staff_account"/> holds that for routes not yet
/// written.
/// </remarks>
public sealed class StaffPasswordRoutesTests : IDisposable
{
    private const string Ip = "10.10.11.27";
    private const string Original = "Granite-lantern-41";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly PasswordPolicySettings _settings = new() { ExpiryExemptRoles = ["ApiUser"] };
    private readonly ManualClock _clock = new();
    private readonly User _user;

    public StaffPasswordRoutesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        // An account whose holder chose its password some time ago.
        _user = new User
        {
            Id = Guid.NewGuid(),
            Username = "wellington.moyo",
            Email = "wellington.moyo@example.test",
            FirstName = "Wellington",
            LastName = "Moyo",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Original, 4),
            PasswordChangedAt = _clock.GetUtcNow().UtcDateTime,
            Role = ApplicationRoles.SalesRep,
            IsActive = true,
            CreatedAt = _clock.GetUtcNow().UtcDateTime
        };
        _context.Users.Add(_user);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── an administrator resetting somebody's password ─────────────────────────────────────────

    [Theory]
    [InlineData("short1!", PasswordPolicy.TooShort)]
    [InlineData("Password123", PasswordPolicy.TooCommon)]
    [InlineData("moyo-lantern-77", PasswordPolicy.ContainsPersonalDetail)]
    [InlineData(Original, UserPasswords.UsedBefore)]
    public async Task An_administrator_cannot_set_a_password_the_policy_refuses(string password, string because)
    {
        var result = await AdminChangeAsync(password);

        Assert.True(result.IsError);
        Assert.Equal("Password.PolicyNotMet", result.FirstError.Code);
        Assert.Contains(because, result.FirstError.Description);
        Assert.True(BCrypt.Net.BCrypt.Verify(Original, Reloaded().PasswordHash));
    }

    [Fact]
    public async Task An_administrators_reset_signs_the_account_out_and_makes_its_holder_choose_their_own()
    {
        var session = AddRefreshToken();

        var result = await AdminChangeAsync("copper-kettle-552");

        Assert.False(result.IsError);
        var saved = Reloaded();
        Assert.True(BCrypt.Net.BCrypt.Verify("copper-kettle-552", saved.PasswordHash));
        Assert.True(saved.MustChangePassword);
        Assert.True(_context.RefreshTokens.AsNoTracking().Single(token => token.Id == session).IsRevoked);
    }

    // ── the van app's own change-password screen, which reuses the administrator's command ────

    [Fact]
    public async Task A_van_rep_changing_their_own_password_is_not_left_owing_another_change()
    {
        _user.MustChangePassword = true;
        _context.SaveChanges();

        var mediator = StubProxy.For<IMediator>((method, args) =>
            args![0] is AdminChangePasswordCommand command
                ? AdminHandler().Handle(command, CancellationToken.None)
                : throw new InvalidOperationException($"Unexpected {method.Name}"));

        var result = await new ChangeVanSalesPasswordHandler(mediator).Handle(
            new ChangeVanSalesPasswordCommand(
                new VanSalesPasswordChangeRequest { Password = "copper-kettle-552" }, _user.Id),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.False(Reloaded().MustChangePassword);
    }

    [Fact]
    public async Task The_van_app_is_told_which_rule_its_new_password_broke()
    {
        var mediator = StubProxy.For<IMediator>((_, args) =>
            AdminHandler().Handle((AdminChangePasswordCommand)args![0]!, CancellationToken.None));

        var result = await new ChangeVanSalesPasswordHandler(mediator).Handle(
            new ChangeVanSalesPasswordCommand(
                new VanSalesPasswordChangeRequest { Password = "wellington1" }, _user.Id),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, result.FirstError.Description);
    }

    // ── the holder changing it, signed in ──────────────────────────────────────────────────────

    [Fact]
    public async Task Changing_your_own_password_needs_the_current_one()
    {
        var result = await ResetService().ChangePasswordAsync(_user.Id, "not-the-password", "Copper-kettle-552!");

        Assert.False(result.IsSuccess);
        Assert.Equal("Current password is incorrect", result.Message);
    }

    [Fact]
    public async Task Wrong_current_passwords_count_towards_the_sign_in_lockout()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await ResetService().ChangePasswordAsync(_user.Id, $"guess-{attempt}", "Copper-kettle-552!");
            Assert.Equal(PasswordResetService.CurrentPasswordIncorrect, wrong.Message);
        }

        var locked = Reloaded();
        Assert.Equal(5, locked.FailedLoginAttempts);
        Assert.True(locked.LockoutEnd > DateTime.UtcNow);

        // Locked out, the right password no longer works here either.
        var result = await ResetService().ChangePasswordAsync(_user.Id, Original, "Copper-kettle-552!");

        Assert.False(result.IsSuccess);
        Assert.Equal(PasswordResetService.AccountLockedOut, result.Message);
        Assert.True(BCrypt.Net.BCrypt.Verify(Original, Reloaded().PasswordHash));
    }

    [Fact]
    public async Task A_deactivated_account_cannot_change_its_password_and_is_answered_as_a_wrong_one()
    {
        _user.IsActive = false;
        _context.SaveChanges();

        var inactive = await ResetService().ChangePasswordAsync(_user.Id, Original, "Copper-kettle-552!");
        var missing = await ResetService().ChangePasswordAsync(Guid.NewGuid(), Original, "Copper-kettle-552!");

        Assert.False(inactive.IsSuccess);
        Assert.Equal(PasswordResetService.CurrentPasswordIncorrect, inactive.Message);
        Assert.Equal(PasswordResetService.CurrentPasswordIncorrect, missing.Message);
        Assert.True(BCrypt.Net.BCrypt.Verify(Original, Reloaded().PasswordHash));
    }

    [Theory]
    [InlineData("Wellington-2026!", PasswordPolicy.ContainsPersonalDetail)]
    [InlineData("P@ssw0rd!", PasswordPolicy.TooCommon)]
    [InlineData(Original, UserPasswords.UsedBefore)]
    // The character mix this route always asked for is still asked for.
    [InlineData("copper-kettle-lantern", "at least one uppercase letter")]
    public async Task Changing_your_own_password_is_held_to_the_policy(string password, string because)
    {
        var result = await ResetService().ChangePasswordAsync(_user.Id, Original, password);

        Assert.False(result.IsSuccess);
        Assert.Contains(because, result.Message);
        Assert.True(BCrypt.Net.BCrypt.Verify(Original, Reloaded().PasswordHash));
    }

    [Fact]
    public async Task Changing_your_own_password_clears_the_flag_restarts_the_clock_and_ends_other_sessions()
    {
        _user.MustChangePassword = true;
        _context.SaveChanges();
        var session = AddRefreshToken();
        _clock.Advance(TimeSpan.FromDays(40));

        var result = await ResetService().ChangePasswordAsync(_user.Id, Original, "Copper-kettle-552!");

        Assert.True(result.IsSuccess, result.Message);
        var saved = Reloaded();
        Assert.True(BCrypt.Net.BCrypt.Verify("Copper-kettle-552!", saved.PasswordHash));
        Assert.False(saved.MustChangePassword);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, saved.PasswordChangedAt);
        Assert.True(_context.RefreshTokens.AsNoTracking().Single(token => token.Id == session).IsRevoked);

        // And the old one cannot be taken back at the next renewal.
        var back = await ResetService().ChangePasswordAsync(_user.Id, "Copper-kettle-552!", Original);
        Assert.Contains(UserPasswords.UsedBefore, back.Message);
    }

    // ── the emailed reset link ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reset_link_cannot_be_used_to_set_a_password_the_policy_refuses()
    {
        var service = ResetService();
        var token = await service.GetResetTokenForTestingAsync(_user.Email!);

        var result = await service.CompleteResetAsync(token!, "Wellington-2026!", Ip);

        Assert.False(result.IsSuccess);
        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, result.Message);

        // The link is not spent by a refused password — the holder gets to try again.
        var retry = await service.CompleteResetAsync(token!, "Copper-kettle-552!", Ip);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.False(Reloaded().MustChangePassword);
    }

    // ── creating an account ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_an_account_with_a_common_password_is_refused_before_anything_is_written()
    {
        var handler = new RegisterHandler(
            StubProxy.Unused<IAuthService>(),
            StubProxy.Unused<IAuditService>(),
            Passwords(),
            NullLogger<RegisterHandler>.Instance);

        var result = await handler.Handle(
            new RegisterCommand("rudo.ncube", "rudo@example.test", "Welcome2026", ApplicationRoles.Cashier, "admin"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Password.PolicyNotMet", result.FirstError.Code);
    }

    [Fact]
    public async Task A_registered_account_is_stamped_and_owes_its_holder_a_password_of_their_own()
    {
        var created = await AuthService().RegisterUserAsync(
            "rudo.ncube", "rudo@example.test", "copper-kettle-552", ApplicationRoles.Cashier);

        Assert.NotNull(created);
        Assert.True(created.MustChangePassword);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, created.PasswordChangedAt);
    }

    // ── requiring a change ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Requiring_a_change_ends_the_sessions_and_the_next_sign_in_says_so()
    {
        var session = AddRefreshToken();

        var result = await new RequirePasswordChangeHandler(
                _context, Passwords(), StubProxy.Unused<IAuditService>(), NullLogger<RequirePasswordChangeHandler>.Instance)
            .Handle(new RequirePasswordChangeCommand(_user.Id), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(_context.RefreshTokens.AsNoTracking().Single(token => token.Id == session).IsRevoked);

        var signIn = await SignInAsync();
        Assert.True(signIn.PasswordChangeRequired);
        Assert.Equal(PasswordStanding.Required, signIn.PasswordChangeReason);
        Assert.Equal(PasswordStanding.Required, ClaimOn(signIn.AccessToken));
    }

    // ── what sign-in reports ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signing_in_with_a_password_in_date_reports_when_it_runs_out_and_marks_nothing()
    {
        var signIn = await SignInAsync();

        Assert.False(signIn.PasswordChangeRequired);
        Assert.Null(signIn.PasswordChangeReason);
        Assert.NotNull(signIn.PasswordExpiresAt);
        Assert.InRange(signIn.PasswordExpiresInDays!.Value, 89, 93);
        Assert.Null(ClaimOn(signIn.AccessToken));
    }

    [Fact]
    public async Task Signing_in_with_a_password_over_three_months_old_succeeds_and_says_a_change_is_owed()
    {
        _clock.Advance(TimeSpan.FromDays(95));

        var signIn = await SignInAsync();

        // The sign-in itself is not refused: the holder has to be able to reach the screen that
        // fixes it, and on a till or a handset that screen is behind the sign-in.
        Assert.False(string.IsNullOrEmpty(signIn.AccessToken));
        Assert.True(signIn.PasswordChangeRequired);
        Assert.Equal(PasswordStanding.Expired, signIn.PasswordChangeReason);
        Assert.Equal(0, signIn.PasswordExpiresInDays);
        Assert.Equal(PasswordStanding.Expired, ClaimOn(signIn.AccessToken));
    }

    [Fact]
    public async Task A_session_kept_alive_by_refreshing_learns_that_its_password_has_expired()
    {
        var service = AuthService();
        var signIn = await service.AuthenticateAsync(Credentials(), Ip);
        Assert.False(signIn!.PasswordChangeRequired);

        _clock.Advance(TimeSpan.FromDays(95));
        // The refresh token itself is checked against the wall clock, and has not lapsed there.
        var refreshed = await service.RefreshTokenAsync(signIn.RefreshToken, Ip);

        Assert.NotNull(refreshed);
        Assert.True(refreshed.PasswordChangeRequired);
        Assert.Equal(PasswordStanding.Expired, ClaimOn(refreshed.AccessToken));
    }

    [Fact]
    public async Task A_service_account_is_never_asked_to_renew()
    {
        _user.Role = ApplicationRoles.ApiUser;
        _context.SaveChanges();
        _clock.Advance(TimeSpan.FromDays(400));

        var signIn = await SignInAsync();

        Assert.False(signIn.PasswordChangeRequired);
        Assert.Null(signIn.PasswordExpiresAt);
    }

    // ── the guard ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the API's source for anything that assigns <c>PasswordHash</c> to something other
    /// than an empty placeholder, outside the files allowed to.
    /// </summary>
    /// <remarks>
    /// Seven places used to. A route written next year that hashes a password itself would compile,
    /// pass every other test here and skip the whole policy — which is how the seven came to
    /// disagree in the first place. The fix when this fails is to call <see cref="UserPasswords"/>,
    /// not to add the file to the list.
    /// </remarks>
    [Fact]
    public void No_other_code_puts_a_password_on_a_staff_account()
    {
        var root = RepositoryRoot();
        var api = Path.Combine(root, "ShopInventory");

        string[] allowed =
        [
            // The writer itself.
            Path.Combine("Features", "Password", "UserPasswords.cs"),
            // Development-only seed accounts, created when the table is empty.
            Path.Combine("Data", "DbInitializer.cs"),
            // The ordering app's shops: a different table and a different kind of account, which
            // applies PasswordPolicy itself.
            Path.Combine("Features", "VanSalesCustomerAuth", "Commands", "OnboardVanSalesCustomerAccount", "OnboardVanSalesCustomerAccountHandler.cs"),
            // Prints a hash for a developer; sets nothing.
            Path.Combine("Features", "CustomerPortal", "Commands", "GeneratePasswordHash", "GeneratePasswordHashHandler.cs")
        ];

        // "PasswordHash = <something>" or "x.PasswordHash, <something>" inside a SetProperty, where
        // the something is not string.Empty and the operator is not a comparison.
        var assignment = new Regex(
            @"PasswordHash\s*=(?!=)(?!\s*string\.Empty\b)|SetProperty\([^)]*PasswordHash",
            RegexOptions.CultureInvariant);

        var offenders = Directory
            .EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "Migrations" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "Backups" + Path.DirectorySeparatorChar))
            .Where(path => !allowed.Any(file => path.EndsWith(file, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, number: index + 1))
                .Where(entry => assignment.IsMatch(entry.line))
                .Select(entry => $"{Path.GetRelativePath(api, path)}:{entry.number}: {entry.line.Trim()}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These lines set a password without going through UserPasswords:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_guard_can_see_the_writer_it_exempts()
    {
        // A scan that matched nothing anywhere would pass for ever. The writer's own assignment is
        // what it must be able to find.
        var writer = Path.Combine(RepositoryRoot(), "ShopInventory", "Features", "Password", "UserPasswords.cs");

        Assert.Contains(File.ReadLines(writer), line => line.Contains("user.PasswordHash = Hash(password);"));
    }

    // ── fixtures ───────────────────────────────────────────────────────────────────────────────

    private UserPasswords Passwords() => TestPasswords.For(_context, _settings, _clock);

    private AdminChangePasswordHandler AdminHandler() =>
        new(_context, StubProxy.Unused<IAuditService>(), Passwords(), NullLogger<AdminChangePasswordHandler>.Instance);

    private Task<ErrorOr.ErrorOr<ErrorOr.Success>> AdminChangeAsync(string password) =>
        AdminHandler().Handle(
            new AdminChangePasswordCommand(_user.Id, new AdminChangePasswordRequest { NewPassword = password }),
            CancellationToken.None);

    private PasswordResetService ResetService() =>
        new(_context,
            NullLogger<PasswordResetService>.Instance,
            Passwords(),
            Options.Create(new SecuritySettings { MaxFailedLoginAttempts = 5, LockoutDurationMinutes = 15 }));

    private AuthService AuthService() =>
        new(_context,
            Options.Create(new JwtSettings
            {
                SecretKey = new string('k', 64),
                Issuer = "ShopInventoryAPI",
                Audience = "ShopInventoryClients",
                AccessTokenExpirationMinutes = 60,
                RefreshTokenExpirationDays = 7
            }),
            Options.Create(new SecuritySettings()),
            NullLogger<AuthService>.Instance,
            StubProxy.Unused<ITwoFactorPendingStore>(),
            StubProxy.Unused<ITwoFactorService>(),
            Passwords());

    private AuthLoginRequest Credentials() => new() { Username = _user.Username, Password = Original };

    private async Task<AuthLoginResponse> SignInAsync()
    {
        var response = await AuthService().AuthenticateAsync(Credentials(), Ip);
        Assert.NotNull(response);
        return response;
    }

    private static string? ClaimOn(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .FirstOrDefault(claim => claim.Type == PasswordStanding.ClaimType)?.Value;

    private User Reloaded() => _context.Users.AsNoTracking().Single(user => user.Id == _user.Id);

    private Guid AddRefreshToken()
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Guid.NewGuid().ToString("N"),
            UserId = _user.Id,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            // Against the wall clock: a session is live or not by real time, not by the test's.
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _context.RefreshTokens.Add(token);
        _context.SaveChanges();
        return token.Id;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("ShopInventory.sln was not found above the test output.");
    }
}
