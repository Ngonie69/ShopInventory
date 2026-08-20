using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the grace window that stops a concurrent refresh from logging a working client out.
/// </summary>
/// <remarks>
/// Refresh tokens rotate on every use, and until this window existed the loser of any race was
/// refused. Production showed it twice in nine hours: a successful refresh at 07:59:37.691 followed
/// 50 ms later by <c>Inactive refresh token used … Expired: false, Revoked: true</c> from the same
/// IP, and a burst of four parallel refreshes at 09:02 that all failed into another eight 401s.
/// Both are legitimate clients holding a token that was valid when the request left.
/// <para>
/// What must not soften is the case the rotation is actually there for: a token replayed long after
/// it was rotated. These tests hold both ends — and the third one holds the hole in between, where
/// a replay every 59 seconds could otherwise keep sliding the window forward forever.
/// </para>
/// </remarks>
public sealed class RefreshTokenRotationGraceTests : IDisposable
{
    private const string Ip = "10.10.11.27";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly CapturingLogger<AuthService> _log = new();
    private readonly Guid _userId = Guid.NewGuid();

    public RefreshTokenRotationGraceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _userId,
            Username = "wellington.moyo",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.SalesRep,
            IsActive = true
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Two_refreshes_racing_on_one_token_both_succeed()
    {
        var service = CreateService();
        var token = await IssueTokenAsync();

        var first = await service.RefreshTokenAsync(token, Ip);
        var second = await service.RefreshTokenAsync(token, Ip);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // Each gets its own pair — only the successor's hash is stored, so there is nothing to
        // replay and the chain forks instead.
        Assert.NotEqual(first!.RefreshToken, second!.RefreshToken);
        Assert.Empty(_log.AtOrAbove(LogLevel.Warning));
    }

    [Fact]
    public async Task A_token_replayed_after_the_window_is_still_refused()
    {
        var service = CreateService();
        var token = await IssueTokenAsync();

        Assert.NotNull(await service.RefreshTokenAsync(token, Ip));

        await RewindRotationAsync(token, TimeSpan.FromSeconds(90));

        Assert.Null(await service.RefreshTokenAsync(token, Ip));
        Assert.Contains(
            _log.AtOrAbove(LogLevel.Warning),
            entry => entry.Message.Contains("Inactive refresh token used"));
    }

    /// <summary>
    /// The window is measured from the original rotation, not from the last use. Re-stamping
    /// <see cref="ShopInventory.Models.RefreshToken.RevokedAt"/> on every reissue would let a
    /// replayed token stay inside a 60-second window indefinitely by refreshing every 59 seconds.
    /// </summary>
    [Fact]
    public async Task Reusing_inside_the_window_does_not_slide_the_window_forward()
    {
        var service = CreateService();
        var token = await IssueTokenAsync();

        Assert.NotNull(await service.RefreshTokenAsync(token, Ip));

        // 50s after the rotation: still inside the 60s window, so this succeeds. If that reissue
        // re-stamps RevokedAt, the token is back to looking freshly rotated.
        await RewindRotationAsync(token, TimeSpan.FromSeconds(50));
        Assert.NotNull(await service.RefreshTokenAsync(token, Ip));

        // 20 more seconds — 70 from the original rotation, so the window has passed. Rewinding by a
        // delta rather than setting an absolute age is the whole point: with the stamp re-written
        // this reads as 20 seconds old and would still be accepted.
        await RewindRotationAsync(token, TimeSpan.FromSeconds(20));
        Assert.Null(await service.RefreshTokenAsync(token, Ip));
    }

    /// <summary>A sign-out revokes without rotating. Honouring that would undo the sign-out.</summary>
    [Fact]
    public async Task A_token_revoked_by_sign_out_is_not_covered_by_the_window()
    {
        var service = CreateService();
        var token = await IssueTokenAsync();

        await service.RevokeTokenAsync(token, Ip);

        Assert.Null(await service.RefreshTokenAsync(token, Ip));
    }

    /// <summary>An expired token is over on its own terms, however recently it was rotated.</summary>
    [Fact]
    public async Task An_expired_token_is_refused_even_just_after_rotation()
    {
        var service = CreateService();
        var token = await IssueTokenAsync();

        Assert.NotNull(await service.RefreshTokenAsync(token, Ip));

        var stored = await LoadAsync(token);
        stored.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _context.SaveChangesAsync();

        Assert.Null(await service.RefreshTokenAsync(token, Ip));
    }

    [Fact]
    public async Task The_window_can_be_switched_off()
    {
        var service = CreateService(graceSeconds: 0);
        var token = await IssueTokenAsync();

        Assert.NotNull(await service.RefreshTokenAsync(token, Ip));
        Assert.Null(await service.RefreshTokenAsync(token, Ip));
    }

    /// <summary>Issues a refresh token the way a login does, and returns its plaintext value.</summary>
    private async Task<string> IssueTokenAsync()
    {
        var value = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        _context.RefreshTokens.Add(new ShopInventory.Models.RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(value),
            UserId = _userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = Ip
        });
        await _context.SaveChangesAsync();

        return value;
    }

    /// <summary>
    /// Moves a token's rotation stamp further into the past, standing in for the passage of time.
    /// </summary>
    /// <remarks>
    /// Relative, not absolute. Setting the stamp to "now minus N" before each assertion would
    /// overwrite whatever the service just wrote, hiding the very thing these tests are checking —
    /// whether a reissue re-stamps the token and slides its window forward.
    /// </remarks>
    private async Task RewindRotationAsync(string tokenValue, TimeSpan delta)
    {
        var stored = await LoadAsync(tokenValue);
        stored.RevokedAt = (stored.RevokedAt ?? DateTime.UtcNow) - delta;
        await _context.SaveChangesAsync();
    }

    private async Task<ShopInventory.Models.RefreshToken> LoadAsync(string tokenValue)
    {
        var hash = Hash(tokenValue);
        return await _context.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
    }

    private static string Hash(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    private AuthService CreateService(int graceSeconds = 60) =>
        new(_context,
            Options.Create(new JwtSettings
            {
                SecretKey = new string('k', 64),
                Issuer = "ShopInventoryAPI",
                Audience = "ShopInventoryClients",
                AccessTokenExpirationMinutes = 60,
                RefreshTokenExpirationDays = 7,
                RefreshTokenRotationGraceSeconds = graceSeconds
            }),
            Options.Create(new SecuritySettings()),
            _log,
            StubProxy.Unused<ITwoFactorPendingStore>(),
            StubProxy.Unused<ITwoFactorService>());
}
