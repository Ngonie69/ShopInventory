using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Password;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the half of the password policy that is about an account over time: what may not be
/// chosen again, when a password has to be renewed, and who has to choose a new one now.
/// </summary>
public sealed class UserPasswordsTests : IDisposable
{
    // 13 October 2026, 08:00 in Harare.
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly SettableClock _clock = new(Start);
    private readonly PasswordPolicySettings _settings = new() { ExpiryExemptRoles = ["ApiUser"] };

    public UserPasswordsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private UserPasswords Passwords => TestPasswords.For(_context, _settings, _clock);

    [Fact]
    public async Task A_new_account_is_stamped_and_owes_a_change_because_someone_else_typed_its_password()
    {
        var user = NewUser("rudo.ncube");

        Assert.Empty(Passwords.CheckForNewAccount(user, "granite-lantern-41"));
        Passwords.SetOnNewAccount(user, "granite-lantern-41");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        Assert.True(BCrypt.Net.BCrypt.Verify("granite-lantern-41", user.PasswordHash));
        Assert.Equal(Start.UtcDateTime, user.PasswordChangedAt);
        Assert.True(user.MustChangePassword);
        Assert.Equal(PasswordStanding.Required, Passwords.StandingOf(user).Reason);
    }

    [Fact]
    public void A_new_account_is_refused_a_password_built_on_its_own_username()
    {
        var user = NewUser("rudo.ncube");

        Assert.Equal(
            [PasswordPolicy.ContainsPersonalDetail],
            Passwords.CheckForNewAccount(user, "ncube-lantern-41"));
    }

    [Fact]
    public async Task The_owner_changing_it_clears_the_flag_and_restarts_the_three_months()
    {
        var user = await SavedUserAsync("granite-lantern-41");
        _clock.Advance(TimeSpan.FromDays(20));

        await ChangeAsync(user, "river-mbira-907", chosenByOwner: true);

        Assert.False(user.MustChangePassword);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, user.PasswordChangedAt);
        Assert.True(BCrypt.Net.BCrypt.Verify("river-mbira-907", user.PasswordHash));
        Assert.False(Passwords.StandingOf(user).ChangeRequired);
    }

    [Fact]
    public async Task An_administrator_resetting_it_leaves_the_owner_owing_a_change_of_their_own()
    {
        var user = await SavedUserAsync("granite-lantern-41");
        await ChangeAsync(user, "river-mbira-907", chosenByOwner: true);

        await ChangeAsync(user, "copper-kettle-552", chosenByOwner: false);

        Assert.True(user.MustChangePassword);
    }

    [Fact]
    public async Task Changing_a_password_ends_every_session_opened_with_the_old_one()
    {
        var user = await SavedUserAsync("granite-lantern-41");
        var live = AddRefreshToken(user, expiresIn: TimeSpan.FromDays(7));
        var lapsed = AddRefreshToken(user, expiresIn: TimeSpan.FromDays(-1));
        await _context.SaveChangesAsync();

        await ChangeAsync(user, "river-mbira-907", chosenByOwner: true);

        Assert.True(live.IsRevoked);
        Assert.Equal("Password changed", live.ReasonRevoked);
        Assert.False(lapsed.IsRevoked);
    }

    [Fact]
    public async Task The_current_password_cannot_be_chosen_again()
    {
        var user = await SavedUserAsync("granite-lantern-41");

        Assert.Equal(
            [UserPasswords.UsedBefore],
            await Passwords.CheckForChangeAsync(user, "granite-lantern-41", CancellationToken.None));
    }

    [Fact]
    public async Task A_recent_password_cannot_be_brought_back_and_an_old_enough_one_can()
    {
        // Remembered: the current one and the four before it.
        var user = await SavedUserAsync("first-lantern-101");
        foreach (var next in new[] { "second-lantern-202", "third-lantern-303", "fourth-lantern-404", "fifth-lantern-505" })
        {
            _clock.Advance(TimeSpan.FromDays(1));
            await ChangeAsync(user, next, chosenByOwner: true);
        }

        Assert.Equal([UserPasswords.UsedBefore], await CheckAsync(user, "first-lantern-101"));
        Assert.Equal([UserPasswords.UsedBefore], await CheckAsync(user, "third-lantern-303"));

        _clock.Advance(TimeSpan.FromDays(1));
        await ChangeAsync(user, "sixth-lantern-606", chosenByOwner: true);

        // The first has now dropped out of the five; the second is the oldest still held.
        Assert.Empty(await CheckAsync(user, "first-lantern-101"));
        Assert.Equal([UserPasswords.UsedBefore], await CheckAsync(user, "second-lantern-202"));
        Assert.Equal(4, await _context.UserPasswordHistory.CountAsync(history => history.UserId == user.Id));
    }

    [Fact]
    public async Task An_account_whose_hash_cannot_be_read_can_still_be_given_a_password()
    {
        // Imported and seeded rows exist with hashes BCrypt refuses to parse. Comparing against one
        // must be a "no", not an exception that stops an administrator repairing the account.
        var user = NewUser("legacy.row");
        user.PasswordHash = "not-a-real-hash";
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        Assert.Empty(await CheckAsync(user, "granite-lantern-41"));
        await ChangeAsync(user, "granite-lantern-41", chosenByOwner: true);
        Assert.Empty(await CheckAsync(user, "river-mbira-907"));
    }

    [Fact]
    public async Task A_password_is_good_through_the_day_three_months_on_and_expires_that_midnight_in_Harare()
    {
        // Set at 08:00 on 13 October, Harare time.
        var user = await SavedOwnedUserAsync();

        var standing = Passwords.StandingOf(user);
        Assert.False(standing.ChangeRequired);
        // 14 January 00:00 CAT is 13 January 22:00 UTC.
        Assert.Equal(new DateTime(2027, 1, 13, 22, 0, 0, DateTimeKind.Utc), standing.ExpiresAtUtc);

        // 13 January, 23:59 in Harare: still the last good day.
        _clock.SetUtcNow(new DateTimeOffset(2027, 1, 13, 21, 59, 0, TimeSpan.Zero));
        Assert.False(Passwords.StandingOf(user).ChangeRequired);
        Assert.Equal(1, Passwords.StandingOf(user).ExpiresInDays);

        // Midnight.
        _clock.SetUtcNow(new DateTimeOffset(2027, 1, 13, 22, 0, 0, TimeSpan.Zero));
        var lapsed = Passwords.StandingOf(user);
        Assert.True(lapsed.ChangeRequired);
        Assert.Equal(PasswordStanding.Expired, lapsed.Reason);
        Assert.Equal(0, lapsed.ExpiresInDays);
    }

    [Fact]
    public async Task The_warning_starts_fourteen_days_out_and_stops_once_the_change_is_owed()
    {
        var user = await SavedOwnedUserAsync();
        var expiry = Passwords.StandingOf(user).ExpiresAtUtc!.Value;

        _clock.SetUtcNow(new DateTimeOffset(expiry.AddDays(-15), TimeSpan.Zero));
        Assert.False(Passwords.IsExpiringSoon(Passwords.StandingOf(user)));

        _clock.SetUtcNow(new DateTimeOffset(expiry.AddDays(-14), TimeSpan.Zero));
        Assert.True(Passwords.IsExpiringSoon(Passwords.StandingOf(user)));

        _clock.SetUtcNow(new DateTimeOffset(expiry.AddDays(1), TimeSpan.Zero));
        Assert.False(Passwords.IsExpiringSoon(Passwords.StandingOf(user)));
    }

    [Fact]
    public async Task A_row_with_no_change_date_is_aged_from_when_the_account_was_created()
    {
        var user = await SavedOwnedUserAsync();
        user.PasswordChangedAt = null;
        user.CreatedAt = Start.UtcDateTime.AddMonths(-4);

        Assert.Equal(PasswordStanding.Expired, Passwords.StandingOf(user).Reason);
    }

    [Fact]
    public async Task A_service_account_never_expires_and_is_not_flagged_when_it_is_created()
    {
        var api = NewUser("sap-bridge", ApplicationRoles.ApiUser);
        Passwords.SetOnNewAccount(api, "granite-lantern-41");
        _context.Users.Add(api);
        await _context.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(400));
        var standing = Passwords.StandingOf(api);

        Assert.False(api.MustChangePassword);
        Assert.False(standing.ChangeRequired);
        Assert.Null(standing.ExpiresAtUtc);
        Assert.Null(standing.ExpiresInDays);
    }

    [Fact]
    public async Task Renewal_can_be_switched_off_without_touching_the_rest()
    {
        _settings.MaxAgeMonths = 0;
        var user = await SavedOwnedUserAsync();

        _clock.Advance(TimeSpan.FromDays(400));

        Assert.False(Passwords.StandingOf(user).ChangeRequired);
    }

    [Fact]
    public async Task Requiring_a_change_flags_the_account_and_signs_it_out_everywhere()
    {
        var user = await SavedOwnedUserAsync();
        var session = AddRefreshToken(user, expiresIn: TimeSpan.FromDays(7));
        await _context.SaveChangesAsync();

        await Passwords.RequireChangeAsync(user, CancellationToken.None);
        await _context.SaveChangesAsync();

        Assert.True(user.MustChangePassword);
        Assert.True(session.IsRevoked);
        Assert.Equal(PasswordStanding.Required, Passwords.StandingOf(user).Reason);
    }

    private static User NewUser(string username, string role = ApplicationRoles.Cashier) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = string.Empty,
        Role = role,
        IsActive = true,
        CreatedAt = Start.UtcDateTime
    };

    private async Task<User> SavedUserAsync(string password)
    {
        var user = NewUser("rudo.ncube");
        Passwords.SetOnNewAccount(user, password);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    /// <summary>An account whose holder has already chosen their own password.</summary>
    private async Task<User> SavedOwnedUserAsync()
    {
        var user = await SavedUserAsync("granite-lantern-41");
        await ChangeAsync(user, "river-mbira-907", chosenByOwner: true);
        return user;
    }

    private async Task ChangeAsync(User user, string password, bool chosenByOwner)
    {
        await Passwords.ChangeAsync(
            user,
            password,
            chosenByOwner,
            chosenByOwner ? "Password changed" : "Password reset by administrator",
            CancellationToken.None);
        await _context.SaveChangesAsync();
    }

    private Task<IReadOnlyList<string>> CheckAsync(User user, string password) =>
        Passwords.CheckForChangeAsync(user, password, CancellationToken.None);

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }

    private RefreshToken AddRefreshToken(User user, TimeSpan expiresIn)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            ExpiresAt = _clock.GetUtcNow().UtcDateTime.Add(expiresIn)
        };
        _context.RefreshTokens.Add(token);
        return token;
    }
}
