using System.Security.Cryptography;
using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The second step of a two-factor sign-in says which check failed.
/// </summary>
/// <remarks>
/// Until 2026-10-31 every failure here answered "Invalid username or password", after the password
/// had already been accepted, so an admin signing in through Swagger could not tell an expired
/// challenge from a wrong code. Every refusal must stay an Unauthorized error (403), because the
/// KefShop till treats any 401/403 on this route as "code not accepted" and keeps the challenge open.
/// <para>
/// Real <see cref="AuthService"/>, <see cref="TwoFactorService"/> and <see cref="TwoFactorPendingStore"/>
/// on SQLite. Authenticator codes come from the RFC 6238 implementation at the bottom of this file,
/// written separately from the service's, so a test passing means the two agree.
/// </para>
/// </remarks>
public sealed class TwoFactorLoginRefusalTests : IDisposable
{
    private const string Ip = "10.10.11.40";
    private const int MaxAttempts = 3;
    private const int LockoutMinutes = 15;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly TwoFactorPendingStore _pendingStore = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _secret = NewBase32Secret();

    public TwoFactorLoginRefusalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _userId,
            Username = "tendai.admin",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Admin,
            IsActive = true,
            TwoFactorEnabled = true,
            TwoFactorSecret = _dataProtection.CreateProtector("ShopInventory.TwoFactor.Secret").Protect(_secret)
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task The_right_code_signs_in()
    {
        var result = await CompleteAsync(_pendingStore.CreateChallenge(_userId), CurrentCode());

        Assert.False(result.IsError, result.IsError ? result.FirstError.Code : "");
        Assert.False(string.IsNullOrEmpty(result.Value.AccessToken));
    }

    [Fact]
    public async Task An_unknown_challenge_says_the_sign_in_expired()
    {
        // What a restart, a deploy or the five-minute expiry all leave behind: a token the
        // process no longer holds.
        var result = await CompleteAsync("token-this-process-never-issued", CurrentCode());

        ShouldRefuse(result, Errors.Auth.TwoFactorChallengeExpired);
    }

    [Fact]
    public async Task A_challenge_replaced_by_a_newer_sign_in_says_the_sign_in_expired()
    {
        var first = _pendingStore.CreateChallenge(_userId);
        _pendingStore.CreateChallenge(_userId);

        ShouldRefuse(await CompleteAsync(first, CurrentCode()), Errors.Auth.TwoFactorChallengeExpired);
    }

    [Fact]
    public async Task A_wrong_code_says_so_and_leaves_the_challenge_open()
    {
        var challenge = _pendingStore.CreateChallenge(_userId);

        ShouldRefuse(await CompleteAsync(challenge, WrongCode()), Errors.Auth.TwoFactorCodeInvalid);

        var retry = await CompleteAsync(challenge, CurrentCode());
        Assert.False(retry.IsError, "the till re-prompts on the same challenge after a wrong code");
    }

    [Fact]
    public async Task A_code_spent_on_an_earlier_sign_in_says_it_was_already_used()
    {
        var code = CurrentCode();
        Assert.False((await CompleteAsync(_pendingStore.CreateChallenge(_userId), code)).IsError);

        ShouldRefuse(
            await CompleteAsync(_pendingStore.CreateChallenge(_userId), code),
            Errors.Auth.TwoFactorCodeAlreadyUsed);
    }

    [Fact]
    public async Task The_wrong_code_that_locks_the_account_says_it_is_locked()
    {
        var challenge = _pendingStore.CreateChallenge(_userId);
        for (var attempt = 1; attempt < MaxAttempts; attempt++)
        {
            ShouldRefuse(await CompleteAsync(challenge, WrongCode()), Errors.Auth.TwoFactorCodeInvalid);
        }

        var locking = await CompleteAsync(challenge, WrongCode());

        ShouldRefuse(locking, Errors.Auth.TwoFactorLockedOut(LockoutMinutes));
        Assert.Contains($"{LockoutMinutes} minutes", locking.FirstError.Description);
    }

    [Fact]
    public async Task A_locked_account_refuses_even_the_right_code()
    {
        var user = await _context.Users.SingleAsync(u => u.Id == _userId);
        user.LockoutEnd = DateTime.UtcNow.AddMinutes(4).AddSeconds(30);
        await _context.SaveChangesAsync();

        var result = await CompleteAsync(_pendingStore.CreateChallenge(_userId), CurrentCode());

        ShouldRefuse(result, Errors.Auth.TwoFactorLockedOut(5));
        Assert.Contains("5 minutes", result.FirstError.Description);
    }

    [Fact]
    public async Task A_wrong_backup_code_says_so()
    {
        var codes = await NewTwoFactorService().RegenerateBackupCodesAsync(_userId, CurrentCode());
        Assert.NotEmpty(codes);

        ShouldRefuse(
            await CompleteAsync(_pendingStore.CreateChallenge(_userId), "ZZZZ-ZZZZ", isBackupCode: true),
            Errors.Auth.TwoFactorBackupCodeInvalid);
    }

    [Fact]
    public async Task A_backup_code_on_an_account_with_none_left_says_there_are_none()
    {
        ShouldRefuse(
            await CompleteAsync(_pendingStore.CreateChallenge(_userId), "A1B2-C3D4", isBackupCode: true),
            Errors.Auth.TwoFactorNoBackupCodes);
    }

    [Fact]
    public async Task Two_factor_turned_off_between_the_steps_says_to_sign_in_again()
    {
        var challenge = _pendingStore.CreateChallenge(_userId);
        var user = await _context.Users.SingleAsync(u => u.Id == _userId);
        user.TwoFactorEnabled = false;
        await _context.SaveChangesAsync();

        ShouldRefuse(await CompleteAsync(challenge, CurrentCode()), Errors.Auth.TwoFactorChallengeExpired);
    }

    private static void ShouldRefuse(ErrorOr<ShopInventory.DTOs.AuthLoginResponse> result, Error expected)
    {
        Assert.True(result.IsError);
        Assert.Equal(expected.Code, result.FirstError.Code);
        Assert.Equal(expected.Description, result.FirstError.Description);

        // ApiControllerBase maps Unauthorized to 403, which is what the till's re-prompt relies on.
        Assert.Equal(ErrorType.Unauthorized, result.FirstError.Type);
    }

    private Task<ErrorOr<ShopInventory.DTOs.AuthLoginResponse>> CompleteAsync(
        string challenge, string code, bool isBackupCode = false) =>
        CreateAuthService().CompleteTwoFactorLoginAsync(challenge, code, isBackupCode, Ip, CancellationToken.None);

    private static IOptions<SecuritySettings> Security() => Options.Create(new SecuritySettings
    {
        MaxFailedLoginAttempts = MaxAttempts,
        LockoutDurationMinutes = LockoutMinutes
    });

    private TwoFactorService NewTwoFactorService() =>
        new(_context, NullLogger<TwoFactorService>.Instance, Security(), _dataProtection);

    private AuthService CreateAuthService() =>
        new(_context,
            Options.Create(new JwtSettings
            {
                SecretKey = new string('k', 64),
                Issuer = "ShopInventoryAPI",
                Audience = "ShopInventoryClients",
                AccessTokenExpirationMinutes = 60,
                RefreshTokenExpirationDays = 7
            }),
            Security(),
            NullLogger<AuthService>.Instance,
            _pendingStore,
            NewTwoFactorService(),
            TestPasswords.For(_context));

    private string CurrentCode() => Totp(_secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    /// <summary>A six-digit code that matches none of the three steps the service accepts.</summary>
    private string WrongCode()
    {
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var accepted = new[] { Totp(_secret, step - 1), Totp(_secret, step), Totp(_secret, step + 1) };
        var candidate = int.Parse(accepted[1]);
        string code;
        do
        {
            candidate = (candidate + 1) % 1_000_000;
            code = candidate.ToString("D6");
        }
        while (accepted.Contains(code));

        return code;
    }

    // ---- RFC 6238 / RFC 4648, independent of TwoFactorService ----------------------------------

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string NewBase32Secret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var output = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in value.TrimEnd('='))
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    private static string Totp(string base32Secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(Base32Decode(base32Secret), counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }
}
