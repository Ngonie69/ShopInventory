using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Service for Two-Factor Authentication operations
/// </summary>
public interface ITwoFactorService
{
    /// <summary>
    /// Initialize 2FA setup for a user
    /// </summary>
    Task<TwoFactorSetupResponse> InitiateSetupAsync(Guid userId);

    /// <summary>
    /// Enable 2FA after verifying the code
    /// </summary>
    Task<List<string>> EnableTwoFactorAsync(Guid userId, string code);

    /// <summary>
    /// Disable 2FA for a user
    /// </summary>
    Task<ServiceResult> DisableTwoFactorAsync(Guid userId, string password, string code);

    /// <summary>
    /// Verify a TOTP code
    /// </summary>
    Task<ServiceResult> VerifyCodeAsync(Guid userId, string code, bool isBackupCode = false);

    /// <summary>
    /// Get 2FA status for a user
    /// </summary>
    Task<TwoFactorStatusResponse> GetStatusAsync(Guid userId);

    /// <summary>
    /// Regenerate backup codes
    /// </summary>
    Task<List<string>> RegenerateBackupCodesAsync(Guid userId, string code);

    /// <summary>
    /// Validate a TOTP code against a secret (used during login)
    /// </summary>
    bool ValidateCode(string secret, string code);
}

/// <summary>
/// Implementation of Two-Factor Authentication service using TOTP
/// </summary>
public class TwoFactorService : ITwoFactorService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<TwoFactorService> _logger;
    private readonly SecuritySettings _securitySettings;
    private readonly IDataProtector _secretProtector;
    private const int SecretKeyLength = 20;
    private const int BackupCodeCount = 10;
    private const string Issuer = "ShopInventory";

    public TwoFactorService(
        ApplicationDbContext context,
        ILogger<TwoFactorService> logger,
        IOptions<SecuritySettings> securitySettings,
        IDataProtectionProvider dataProtectionProvider)
    {
        _context = context;
        _logger = logger;
        _securitySettings = securitySettings.Value;
        _secretProtector = dataProtectionProvider.CreateProtector("ShopInventory.TwoFactor.Secret");
    }

    public async Task<TwoFactorSetupResponse> InitiateSetupAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        if (user.TwoFactorEnabled)
        {
            throw new InvalidOperationException("Two-factor authentication is already enabled. Disable it before starting setup again.");
        }

        // Generate a new secret key
        var secretKey = GenerateSecretKey();

        // Store a protected pending secret and only activate 2FA after the first code is verified.
        user.TwoFactorSecret = ProtectSecret(secretKey);
        user.TwoFactorBackupCodes = null;
        user.TwoFactorLastUsedTimeStep = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Create TOTP URI for QR code
        var totpUri = GenerateTotpUri(secretKey, user.Username);

        return new TwoFactorSetupResponse
        {
            SecretKey = secretKey,
            QrCodeUri = totpUri,
            ManualEntryKey = FormatForManualEntry(secretKey),
            BackupCodes = new List<string>()
        };
    }

    public async Task<List<string>> EnableTwoFactorAsync(Guid userId, string code)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        if (user.TwoFactorEnabled)
        {
            throw new InvalidOperationException("Two-factor authentication is already enabled");
        }

        if (!TryGetSecret(user, out var secret, out var requiresSecretMigration))
        {
            throw new InvalidOperationException("Two-factor setup not initiated. Please start setup first.");
        }

        var verificationResult = await VerifyTotpCodeAsync(user, secret, code, requiresSecretMigration);
        if (!verificationResult.IsSuccess)
        {
            throw new InvalidOperationException(verificationResult.Message);
        }

        // Generate and store backup codes
        var backupCodes = GenerateBackupCodes();
        user.TwoFactorBackupCodes = SerializeBackupCodes(backupCodes);
        user.TwoFactorSecret = ProtectSecret(secret);
        user.TwoFactorEnabled = true;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Two-factor authentication enabled for user {UserId}", userId);

        return backupCodes;
    }

    public async Task<ServiceResult> DisableTwoFactorAsync(Guid userId, string password, string code)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        if (!user.TwoFactorEnabled)
        {
            return ServiceResult.Failure("Two-factor authentication is not enabled");
        }

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return ServiceResult.Failure("Invalid password");
        }

        if (!TryGetSecret(user, out var secret, out var requiresSecretMigration))
        {
            return ServiceResult.Failure("Two-factor authentication not properly configured");
        }

        // Verify 2FA code
        var verificationResult = await VerifyTotpCodeAsync(user, secret, code, requiresSecretMigration);
        if (!verificationResult.IsSuccess)
        {
            return verificationResult;
        }

        // Disable 2FA
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorBackupCodes = null;
        user.TwoFactorLastUsedTimeStep = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Two-factor authentication disabled for user {UserId}", userId);

        return ServiceResult.Success("Two-factor authentication disabled successfully");
    }

    public async Task<ServiceResult> VerifyCodeAsync(Guid userId, string code, bool isBackupCode = false)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        if (!user.TwoFactorEnabled)
        {
            return ServiceResult.Failure("Two-factor authentication is not enabled");
        }

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            return ServiceResult.Failure("Too many failed two-factor attempts. Please try again later.");
        }

        if (isBackupCode)
        {
            return await VerifyBackupCodeAsync(user, code);
        }

        if (!TryGetSecret(user, out var secret, out var requiresSecretMigration))
        {
            return ServiceResult.Failure("Two-factor authentication not properly configured");
        }

        return await VerifyTotpCodeAsync(user, secret, code, requiresSecretMigration);
    }

    public async Task<TwoFactorStatusResponse> GetStatusAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        var backupCodesRemaining = 0;
        if (!string.IsNullOrEmpty(user.TwoFactorBackupCodes))
        {
            try
            {
                var codes = DeserializeBackupCodes(user.TwoFactorBackupCodes);
                backupCodesRemaining = codes?.Count ?? 0;
            }
            catch
            {
                // Ignore deserialization errors
            }
        }

        return new TwoFactorStatusResponse
        {
            IsEnabled = user.TwoFactorEnabled,
            BackupCodesRemaining = backupCodesRemaining,
            EnabledAt = user.TwoFactorEnabled ? user.UpdatedAt : null
        };
    }

    public async Task<List<string>> RegenerateBackupCodesAsync(Guid userId, string code)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        if (!user.TwoFactorEnabled || string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            throw new InvalidOperationException("Two-factor authentication is not enabled");
        }

        if (!TryGetSecret(user, out var secret, out var requiresSecretMigration))
        {
            throw new InvalidOperationException("Two-factor authentication not properly configured");
        }

        var verificationResult = await VerifyTotpCodeAsync(user, secret, code, requiresSecretMigration);
        if (!verificationResult.IsSuccess)
        {
            throw new InvalidOperationException(verificationResult.Message);
        }

        var backupCodes = GenerateBackupCodes();
        user.TwoFactorBackupCodes = SerializeBackupCodes(backupCodes);
        user.TwoFactorSecret = ProtectSecret(secret);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Backup codes regenerated for user {UserId}", userId);

        return backupCodes;
    }

    public bool ValidateCode(string secret, string code)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(code))
        {
            return false;
        }

        return TryGetMatchingTimeStep(secret, code, out _);
    }

    #region Private Methods

    private async Task<ServiceResult> VerifyBackupCodeAsync(User user, string code)
    {
        if (string.IsNullOrEmpty(user.TwoFactorBackupCodes))
        {
            return ServiceResult.Failure("No backup codes available");
        }

        try
        {
            var codes = DeserializeBackupCodes(user.TwoFactorBackupCodes);
            var (storedCodes, backupCodesMigrated) = MigrateLegacyBackupCodes(codes);
            if (codes == null || codes.Count == 0)
            {
                return ServiceResult.Failure("No backup codes available");
            }

            // Normalize the code
            var normalizedCode = NormalizeBackupCode(code);

            var matchingCode = storedCodes.FirstOrDefault(c => VerifyStoredBackupCode(c, normalizedCode));

            if (matchingCode != null)
            {
                // Remove the used code
                storedCodes.Remove(matchingCode);
                user.TwoFactorBackupCodes = SerializeStoredBackupCodes(storedCodes);
                user.FailedLoginAttempts = 0;
                user.LockoutEnd = null;
                user.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogWarning("Backup code used for user {UserId}. Remaining: {Count}", user.Id, codes.Count);

                return ServiceResult.Success("Backup code verified successfully");
            }

            if (backupCodesMigrated)
            {
                user.TwoFactorBackupCodes = SerializeStoredBackupCodes(storedCodes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying backup code for user {UserId}", user.Id);
        }

        await RecordFailedTwoFactorAttemptAsync(user);

        return ServiceResult.Failure("Invalid backup code");
    }

    private async Task<ServiceResult> VerifyTotpCodeAsync(User user, string secret, string code, bool requiresSecretMigration)
    {
        var backupCodesMigrated = TryMigrateLegacyBackupCodes(user);

        if (!TryGetMatchingTimeStep(secret, code, out var timeStep))
        {
            await RecordFailedTwoFactorAttemptAsync(user);
            return ServiceResult.Failure("Invalid verification code");
        }

        if (user.TwoFactorLastUsedTimeStep == timeStep)
        {
            return ServiceResult.Failure("This verification code was already used. Wait for a new code and try again.");
        }

        user.TwoFactorLastUsedTimeStep = timeStep;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;

        if (requiresSecretMigration)
        {
            user.TwoFactorSecret = ProtectSecret(secret);
        }

        if (backupCodesMigrated)
        {
            user.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return ServiceResult.Success("Code verified successfully");
    }

    private async Task RecordFailedTwoFactorAttemptAsync(User user)
    {
        user.FailedLoginAttempts++;
        user.UpdatedAt = DateTime.UtcNow;

        if (user.FailedLoginAttempts >= _securitySettings.MaxFailedLoginAttempts)
        {
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(_securitySettings.LockoutDurationMinutes);
            _logger.LogWarning(
                "User {UserId} locked out after {AttemptCount} failed two-factor attempts",
                user.Id,
                user.FailedLoginAttempts);
        }

        await _context.SaveChangesAsync();
    }

    private bool TryGetSecret(User user, out string secret, out bool requiresSecretMigration)
    {
        secret = string.Empty;
        requiresSecretMigration = false;

        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret))
        {
            return false;
        }

        try
        {
            secret = _secretProtector.Unprotect(user.TwoFactorSecret);
            return true;
        }
        catch
        {
            if (!LooksLikeLegacySecret(user.TwoFactorSecret))
            {
                return false;
            }

            secret = user.TwoFactorSecret;
            requiresSecretMigration = true;
            return true;
        }
    }

    private string ProtectSecret(string secret)
    {
        return _secretProtector.Protect(secret);
    }

    private static bool LooksLikeLegacySecret(string secret)
    {
        return secret.Length >= 16 && secret.All(c =>
            (c >= 'A' && c <= 'Z') ||
            (c >= '2' && c <= '7'));
    }

    private static bool TryGetMatchingTimeStep(string secret, string code, out long matchingTimeStep)
    {
        matchingTimeStep = 0;

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalizedCode = code.Trim();
        var currentTime = GetCurrentTimeStep();

        for (var i = -1; i <= 1; i++)
        {
            var timeStep = currentTime + i;
            var computedCode = ComputeTotp(secret, timeStep);
            if (normalizedCode == computedCode)
            {
                matchingTimeStep = timeStep;
                return true;
            }
        }

        return false;
    }

    private static string SerializeBackupCodes(IEnumerable<string> backupCodes)
    {
        return JsonSerializer.Serialize(backupCodes.Select(HashBackupCode).ToList());
    }

    private static string SerializeStoredBackupCodes(IEnumerable<string> storedCodes)
    {
        return JsonSerializer.Serialize(storedCodes.ToList());
    }

    private static List<string> DeserializeBackupCodes(string? serializedCodes)
    {
        if (string.IsNullOrWhiteSpace(serializedCodes))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(serializedCodes) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string HashBackupCode(string code)
    {
        var normalizedCode = NormalizeBackupCode(code);
        var salt = RandomNumberGenerator.GetBytes(16);
        var saltText = Convert.ToBase64String(salt);
        var payload = Encoding.UTF8.GetBytes($"{normalizedCode}:{saltText}");
        var hash = SHA256.HashData(payload);
        return $"{saltText}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyStoredBackupCode(string storedCode, string normalizedCode)
    {
        var separatorIndex = storedCode.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == storedCode.Length - 1)
        {
            return NormalizeBackupCode(storedCode) == normalizedCode;
        }

        var saltText = storedCode[..separatorIndex];
        var hashText = storedCode[(separatorIndex + 1)..];

        byte[] expectedHash;
        byte[] providedHash;

        try
        {
            expectedHash = Convert.FromBase64String(hashText);
            providedHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedCode}:{saltText}"));
        }
        catch
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    private static (List<string> StoredCodes, bool Migrated) MigrateLegacyBackupCodes(List<string> storedCodes)
    {
        var migratedCodes = new List<string>(storedCodes.Count);
        var migrated = false;

        foreach (var storedCode in storedCodes)
        {
            if (IsStoredBackupCodeHashed(storedCode))
            {
                migratedCodes.Add(storedCode);
                continue;
            }

            migratedCodes.Add(HashBackupCode(storedCode));
            migrated = true;
        }

        return (migratedCodes, migrated);
    }

    private static bool IsStoredBackupCodeHashed(string storedCode)
    {
        var separatorIndex = storedCode.IndexOf(':');
        return separatorIndex > 0 && separatorIndex < storedCode.Length - 1;
    }

    private static bool TryMigrateLegacyBackupCodes(User user)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorBackupCodes))
        {
            return false;
        }

        var (storedCodes, migrated) = MigrateLegacyBackupCodes(DeserializeBackupCodes(user.TwoFactorBackupCodes));
        if (!migrated)
        {
            return false;
        }

        user.TwoFactorBackupCodes = SerializeStoredBackupCodes(storedCodes);
        return true;
    }

    private static string NormalizeBackupCode(string code)
    {
        return code.Replace("-", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
    }

    private static string GenerateSecretKey()
    {
        var key = new byte[SecretKeyLength];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(key);
        return Base32Encode(key);
    }

    private static List<string> GenerateBackupCodes()
    {
        var codes = new List<string>();
        using var rng = RandomNumberGenerator.Create();

        for (var i = 0; i < BackupCodeCount; i++)
        {
            var codeBytes = new byte[5];
            rng.GetBytes(codeBytes);
            var code = BitConverter.ToString(codeBytes).Replace("-", "");
            // Format as XXXX-XXXX
            codes.Add($"{code[..4]}-{code[4..]}");
        }

        return codes;
    }

    private static string GenerateTotpUri(string secret, string accountName)
    {
        var encodedIssuer = Uri.EscapeDataString(Issuer);
        var encodedAccount = Uri.EscapeDataString(accountName);
        return $"otpauth://totp/{encodedIssuer}:{encodedAccount}?secret={secret}&issuer={encodedIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    private static string FormatForManualEntry(string secret)
    {
        // Format in groups of 4 for easier manual entry
        var sb = new StringBuilder();
        for (var i = 0; i < secret.Length; i += 4)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(secret.AsSpan(i, Math.Min(4, secret.Length - i)));
        }
        return sb.ToString();
    }

    private static long GetCurrentTimeStep()
    {
        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return unixTime / 30; // 30 second time step
    }

    private static string ComputeTotp(string secret, long timeStep)
    {
        var key = Base32Decode(secret);
        var timeBytes = BitConverter.GetBytes(timeStep);

        // Ensure big-endian
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(timeBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(timeBytes);

        // Dynamic truncation
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24) |
                   ((hash[offset + 1] & 0xFF) << 16) |
                   ((hash[offset + 2] & 0xFF) << 8) |
                   (hash[offset + 3] & 0xFF);

        var otp = code % 1000000;
        return otp.ToString("D6");
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder();
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                result.Append(alphabet[(buffer >> bitsInBuffer) & 0x1F]);
            }
        }

        if (bitsInBuffer > 0)
        {
            result.Append(alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);
        }

        return result.ToString();
    }

    private static byte[] Base32Decode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var c in base32.ToUpperInvariant())
        {
            if (c == '=' || c == ' ') continue;

            var value = alphabet.IndexOf(c);
            if (value < 0) continue;

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)(buffer >> bitsInBuffer));
            }
        }

        return output.ToArray();
    }

    #endregion
}

/// <summary>
/// Generic service result
/// </summary>
public class ServiceResult
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();

    public static ServiceResult Success(string message = "Operation completed successfully")
    {
        return new ServiceResult { IsSuccess = true, Message = message };
    }

    public static ServiceResult Failure(string error)
    {
        return new ServiceResult { IsSuccess = false, Message = error, Errors = new List<string> { error } };
    }

    public static ServiceResult Failure(List<string> errors)
    {
        return new ServiceResult { IsSuccess = false, Message = errors.FirstOrDefault() ?? "Operation failed", Errors = errors };
    }
}
