using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    Task<ServiceResult> EnableTwoFactorAsync(Guid userId, string code);

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
    private const int SecretKeyLength = 20;
    private const int BackupCodeCount = 10;
    private const string Issuer = "ShopInventory";

    public TwoFactorService(ApplicationDbContext context, ILogger<TwoFactorService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<TwoFactorSetupResponse> InitiateSetupAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        // Generate a new secret key
        var secretKey = GenerateSecretKey();

        // Store temporarily (will be confirmed when user verifies)
        user.TwoFactorSecret = secretKey;
        await _context.SaveChangesAsync();

        // Generate backup codes
        var backupCodes = GenerateBackupCodes();

        // Create TOTP URI for QR code
        var totpUri = GenerateTotpUri(secretKey, user.Username);

        return new TwoFactorSetupResponse
        {
            SecretKey = secretKey,
            QrCodeUri = totpUri,
            ManualEntryKey = FormatForManualEntry(secretKey),
            BackupCodes = backupCodes
        };
    }

    public async Task<ServiceResult> EnableTwoFactorAsync(Guid userId, string code)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        if (string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            return ServiceResult.Failure("Two-factor setup not initiated. Please start setup first.");
        }

        // Verify the code
        if (!ValidateCode(user.TwoFactorSecret, code))
        {
            return ServiceResult.Failure("Invalid verification code");
        }

        // Generate and store backup codes
        var backupCodes = GenerateBackupCodes();
        user.TwoFactorBackupCodes = JsonSerializer.Serialize(backupCodes);
        user.TwoFactorEnabled = true;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Two-factor authentication enabled for user {UserId}", userId);

        return ServiceResult.Success("Two-factor authentication enabled successfully");
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

        // Verify 2FA code
        if (!ValidateCode(user.TwoFactorSecret!, code))
        {
            return ServiceResult.Failure("Invalid verification code");
        }

        // Disable 2FA
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorBackupCodes = null;
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

        if (isBackupCode)
        {
            return await VerifyBackupCodeAsync(user, code);
        }

        if (string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            return ServiceResult.Failure("Two-factor authentication not properly configured");
        }

        if (ValidateCode(user.TwoFactorSecret, code))
        {
            return ServiceResult.Success("Code verified successfully");
        }

        return ServiceResult.Failure("Invalid verification code");
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
                var codes = JsonSerializer.Deserialize<List<string>>(user.TwoFactorBackupCodes);
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

        // Verify the code first
        if (!ValidateCode(user.TwoFactorSecret, code))
        {
            throw new InvalidOperationException("Invalid verification code");
        }

        var backupCodes = GenerateBackupCodes();
        user.TwoFactorBackupCodes = JsonSerializer.Serialize(backupCodes);
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

        // TOTP validation with time window
        var currentTime = GetCurrentTimeStep();

        // Check current time step and adjacent ones (30 second window each side)
        for (var i = -1; i <= 1; i++)
        {
            var computedCode = ComputeTotp(secret, currentTime + i);
            if (code == computedCode)
            {
                return true;
            }
        }

        return false;
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
            var codes = JsonSerializer.Deserialize<List<string>>(user.TwoFactorBackupCodes);
            if (codes == null || codes.Count == 0)
            {
                return ServiceResult.Failure("No backup codes available");
            }

            // Normalize the code
            var normalizedCode = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();

            var matchingCode = codes.FirstOrDefault(c =>
                c.Replace("-", "").Replace(" ", "").ToUpperInvariant() == normalizedCode);

            if (matchingCode != null)
            {
                // Remove the used code
                codes.Remove(matchingCode);
                user.TwoFactorBackupCodes = JsonSerializer.Serialize(codes);
                user.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogWarning("Backup code used for user {UserId}. Remaining: {Count}", user.Id, codes.Count);

                return ServiceResult.Success("Backup code verified successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying backup code for user {UserId}", user.Id);
        }

        return ServiceResult.Failure("Invalid backup code");
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
