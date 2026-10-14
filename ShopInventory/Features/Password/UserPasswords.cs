using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Password;

/// <summary>
/// The one place a staff account's password is judged, set and aged.
/// </summary>
/// <remarks>
/// <para>
/// Seven pieces of code used to put a hash on a <see cref="User"/>, each with its own idea of what
/// was acceptable: one asked for a capital and a symbol, two for eight characters, and the rest for
/// nothing. Whichever of them a password went through decided which policy it met. Everything that
/// sets a staff password now comes here, so the answer no longer depends on the route taken.
/// </para>
/// <para>
/// Nothing here saves. Each caller already has its own unit of work — a new account, a reset token
/// being spent — and the password has to land in the same save as the rest of it.
/// </para>
/// </remarks>
public sealed class UserPasswords(
    ApplicationDbContext db,
    IOptions<PasswordPolicySettings> options,
    TimeProvider clock)
{
    public const string UsedBefore =
        "Choose a password this account has not used recently.";

    /// <summary>Matches the sign-in path, so no route produces a cheaper hash than another.</summary>
    private const int WorkFactor = 12;

    private readonly PasswordPolicySettings _settings = options.Value;

    /// <summary>
    /// What a password for an account that does not exist yet gets wrong. Empty when it will do.
    /// </summary>
    public IReadOnlyList<string> CheckForNewAccount(User user, string? password) =>
        PasswordPolicy.Check(password, OwnerOf(user));

    /// <summary>
    /// What a replacement password for an existing account gets wrong. Empty when it will do.
    /// </summary>
    /// <remarks>
    /// The policy's rules first, then reuse — and reuse only for a password that passed the rules,
    /// because each comparison is a deliberate quarter-second of BCrypt and there is no point
    /// spending five of them on <c>password1</c>.
    /// </remarks>
    public async Task<IReadOnlyList<string>> CheckForChangeAsync(
        User user, string? password, CancellationToken cancellationToken)
    {
        var broken = PasswordPolicy.Check(password, OwnerOf(user));
        if (broken.Count > 0)
        {
            return broken;
        }

        if (Matches(password!, user.PasswordHash))
        {
            return [UsedBefore];
        }

        var earlier = await db.UserPasswordHistory
            .AsNoTracking()
            .Where(history => history.UserId == user.Id)
            .OrderByDescending(history => history.ReplacedAt)
            .ThenByDescending(history => history.Id)
            .Take(EarlierPasswordsKept)
            .Select(history => history.PasswordHash)
            .ToListAsync(cancellationToken);

        return earlier.Any(hash => Matches(password!, hash)) ? [UsedBefore] : [];
    }

    /// <summary>
    /// Puts the first password on an account that is about to be added.
    /// </summary>
    /// <param name="user">Not yet saved. Its role and username must already be set.</param>
    /// <param name="password">Already checked with <see cref="CheckForNewAccount"/>.</param>
    public void SetOnNewAccount(User user, string password)
    {
        user.PasswordHash = Hash(password);
        user.PasswordChangedAt = clock.GetUtcNow().UtcDateTime;

        // Whoever is creating the account typed this, so the owner is not the only one who knows it.
        user.MustChangePassword = _settings.RequireChangeAfterAdminSet && !IsExempt(user);
    }

    /// <summary>
    /// Replaces the password on a tracked account, remembers the one it replaces, and ends the
    /// sessions that were opened with it.
    /// </summary>
    /// <param name="user">Tracked by this context.</param>
    /// <param name="password">Already checked with <see cref="CheckForChangeAsync"/>.</param>
    /// <param name="chosenByOwner">
    /// False when an administrator typed it, which leaves the owner owing a change of their own.
    /// </param>
    /// <param name="sessionsEndedBecause">Written on each refresh token as the reason it was revoked.</param>
    /// <param name="cancellationToken">Cancels the reads; nothing is saved here to be left half-done.</param>
    public async Task ChangeAsync(
        User user,
        string password,
        bool chosenByOwner,
        string sessionsEndedBecause,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        db.UserPasswordHistory.Add(new UserPasswordHistoryEntity
        {
            UserId = user.Id,
            PasswordHash = user.PasswordHash,
            ReplacedAt = now
        });

        // Everything older than the window, leaving room for the row just added.
        var forgotten = await db.UserPasswordHistory
            .Where(history => history.UserId == user.Id)
            .OrderByDescending(history => history.ReplacedAt)
            .ThenByDescending(history => history.Id)
            .Skip(Math.Max(EarlierPasswordsKept - 1, 0))
            .ToListAsync(cancellationToken);
        db.UserPasswordHistory.RemoveRange(forgotten);

        user.PasswordHash = Hash(password);
        user.PasswordChangedAt = now;
        user.MustChangePassword = !chosenByOwner && _settings.RequireChangeAfterAdminSet && !IsExempt(user);
        user.UpdatedAt = now;

        // Somebody locked out by guessing their old password has just proved who they are.
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;

        await EndSessionsAsync(user.Id, sessionsEndedBecause, cancellationToken);
    }

    /// <summary>
    /// Makes the owner choose a new password at their next sign-in, and makes that sign-in happen.
    /// </summary>
    /// <remarks>
    /// For a password somebody thinks is known to the wrong person. Flagging the account without
    /// ending its sessions would leave that person signed in for as long as their refresh token
    /// lasted, which is the opposite of immediately.
    /// </remarks>
    public async Task RequireChangeAsync(User user, CancellationToken cancellationToken)
    {
        user.MustChangePassword = true;
        user.UpdatedAt = clock.GetUtcNow().UtcDateTime;

        await EndSessionsAsync(user.Id, "Password change required", cancellationToken);
    }

    /// <summary>
    /// Where the account stands against the renewal rule right now.
    /// </summary>
    public PasswordStanding StandingOf(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var changedAt = user.PasswordChangedAt ?? user.CreatedAt;
        var expiresAt = ExpiryOf(user, changedAt);

        var reason = user.MustChangePassword
            ? PasswordStanding.Required
            : expiresAt is { } due && now >= due
                ? PasswordStanding.Expired
                : null;

        int? daysLeft = expiresAt is { } end
            ? Math.Max((int)Math.Ceiling((end - now).TotalDays), 0)
            : null;

        return new PasswordStanding(changedAt, expiresAt, reason is not null, reason, daysLeft);
    }

    /// <summary>
    /// Whether to start telling the account its renewal is coming.
    /// </summary>
    public bool IsExpiringSoon(PasswordStanding standing) =>
        !standing.ChangeRequired &&
        standing.ExpiresInDays is { } days &&
        days <= _settings.ExpiryWarningDays;

    /// <summary>
    /// The renewal date: the end of the Harare day that falls the configured number of months
    /// after the change.
    /// </summary>
    /// <remarks>
    /// The end of the day and not the same minute three months on, so that a password changed at
    /// 10:32 does not stop working at 10:32 with a van halfway round its route. It lapses overnight,
    /// and is met at the start of a day rather than in the middle of one.
    /// </remarks>
    private DateTime? ExpiryOf(User user, DateTime changedAtUtc)
    {
        if (_settings.MaxAgeMonths <= 0 || IsExempt(user))
        {
            return null;
        }

        var lastGoodDay = AuditService.ToCAT(changedAtUtc).Date.AddMonths(_settings.MaxAgeMonths);
        return AuditService.FromCAT(lastGoodDay.AddDays(1));
    }

    private bool IsExempt(User user) =>
        _settings.ExpiryExemptRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase) ||
        _settings.ExpiryExemptUsernames.Contains(user.Username, StringComparer.OrdinalIgnoreCase);

    /// <summary>The current password counts as one of the remembered ones.</summary>
    private int EarlierPasswordsKept => Math.Max(_settings.RememberedPasswords - 1, 0);

    private async Task EndSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var live = await db.RefreshTokens
            .Where(token => token.UserId == userId && !token.IsRevoked && token.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        foreach (var token in live)
        {
            token.IsRevoked = true;
            token.RevokedAt = now;
            token.ReasonRevoked = reason;
        }
    }

    private static PasswordOwner OwnerOf(User user) => new()
    {
        Username = user.Username,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        PhoneNumber = user.PhoneNumber
    };

    private static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    /// <summary>
    /// False for a hash BCrypt cannot read, not an exception: a row nobody can sign in with is
    /// still a row somebody may need to put a new password on.
    /// </summary>
    private static bool Matches(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
