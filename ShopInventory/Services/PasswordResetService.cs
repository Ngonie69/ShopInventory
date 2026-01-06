using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Service for password reset operations
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Initiate a password reset request
    /// </summary>
    Task<ServiceResult> InitiateResetAsync(string email, string ipAddress);

    /// <summary>
    /// Validate a reset token
    /// </summary>
    Task<ServiceResult> ValidateTokenAsync(string token);

    /// <summary>
    /// Complete the password reset
    /// </summary>
    Task<ServiceResult> CompleteResetAsync(string token, string newPassword, string ipAddress);

    /// <summary>
    /// Change password for logged-in user
    /// </summary>
    Task<ServiceResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);

    /// <summary>
    /// Get the reset token (for sending via email)
    /// </summary>
    Task<string?> GetResetTokenForTestingAsync(string email);
}

/// <summary>
/// Implementation of password reset service
/// </summary>
public class PasswordResetService : IPasswordResetService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PasswordResetService> _logger;
    private readonly IEmailQueueService? _emailService;
    private const int TokenExpirationHours = 24;
    private const int MaxResetAttemptsPerHour = 5;

    public PasswordResetService(
        ApplicationDbContext context,
        ILogger<PasswordResetService> logger,
        IEmailQueueService? emailService = null)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
    }

    public async Task<ServiceResult> InitiateResetAsync(string email, string ipAddress)
    {
        // Rate limiting check
        var recentAttempts = await _context.PasswordResetTokens
            .Where(t => t.RequestedByIp == ipAddress &&
                        t.CreatedAt > DateTime.UtcNow.AddHours(-1))
            .CountAsync();

        if (recentAttempts >= MaxResetAttemptsPerHour)
        {
            _logger.LogWarning("Rate limit exceeded for password reset from IP {IpAddress}", ipAddress);
            // Return success to prevent email enumeration
            return ServiceResult.Success("If an account with that email exists, a password reset link has been sent.");
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower());

        if (user == null)
        {
            // Don't reveal if user exists - return success message
            _logger.LogInformation("Password reset requested for non-existent email: {Email}", email);
            return ServiceResult.Success("If an account with that email exists, a password reset link has been sent.");
        }

        // Invalidate any existing tokens
        var existingTokens = await _context.PasswordResetTokens
            .Where(t => t.UserId == user.Id && !t.IsUsed)
            .ToListAsync();

        foreach (var existingToken in existingTokens)
        {
            existingToken.IsUsed = true;
            existingToken.UsedAt = DateTime.UtcNow;
        }

        // Generate new token
        var token = GenerateResetToken();
        var tokenHash = HashToken(token);

        var resetToken = new PasswordResetToken
        {
            TokenHash = tokenHash,
            UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.AddHours(TokenExpirationHours),
            RequestedByIp = ipAddress,
            CreatedAt = DateTime.UtcNow
        };

        _context.PasswordResetTokens.Add(resetToken);
        await _context.SaveChangesAsync();

        // Queue email
        if (_emailService != null)
        {
            await _emailService.QueueEmailAsync(
                user.Email!,
                "Password Reset Request",
                GenerateResetEmailBody(user.Username, token),
                "PasswordReset"
            );
        }

        _logger.LogInformation("Password reset initiated for user {UserId}", user.Id);

        return ServiceResult.Success("If an account with that email exists, a password reset link has been sent.");
    }

    public async Task<ServiceResult> ValidateTokenAsync(string token)
    {
        var tokenHash = HashToken(token);

        var resetToken = await _context.PasswordResetTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        if (resetToken == null)
        {
            return ServiceResult.Failure("Invalid or expired reset token");
        }

        if (resetToken.IsUsed)
        {
            return ServiceResult.Failure("This reset link has already been used");
        }

        if (resetToken.ExpiresAt < DateTime.UtcNow)
        {
            return ServiceResult.Failure("This reset link has expired");
        }

        return ServiceResult.Success("Token is valid");
    }

    public async Task<ServiceResult> CompleteResetAsync(string token, string newPassword, string ipAddress)
    {
        var tokenHash = HashToken(token);

        var resetToken = await _context.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        if (resetToken == null)
        {
            return ServiceResult.Failure("Invalid or expired reset token");
        }

        if (resetToken.IsUsed)
        {
            return ServiceResult.Failure("This reset link has already been used");
        }

        if (resetToken.ExpiresAt < DateTime.UtcNow)
        {
            return ServiceResult.Failure("This reset link has expired");
        }

        // Validate password strength
        var validationResult = ValidatePasswordStrength(newPassword);
        if (!validationResult.IsSuccess)
        {
            return validationResult;
        }

        // Update password
        var user = resetToken.User;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;

        // Clear lockout if any
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;

        // Mark token as used
        resetToken.IsUsed = true;
        resetToken.UsedAt = DateTime.UtcNow;

        // Invalidate all refresh tokens
        var refreshTokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == user.Id && !rt.IsRevoked)
            .ToListAsync();

        foreach (var rt in refreshTokens)
        {
            rt.IsRevoked = true;
            rt.RevokedAt = DateTime.UtcNow;
            rt.RevokedByIp = ipAddress;
            rt.ReasonRevoked = "Password reset";
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Password reset completed for user {UserId}", user.Id);

        return ServiceResult.Success("Password has been reset successfully. You can now log in with your new password.");
    }

    public async Task<ServiceResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            return ServiceResult.Failure("Current password is incorrect");
        }

        // Validate new password strength
        var validationResult = ValidatePasswordStrength(newPassword);
        if (!validationResult.IsSuccess)
        {
            return validationResult;
        }

        // Check that new password is different
        if (BCrypt.Net.BCrypt.Verify(newPassword, user.PasswordHash))
        {
            return ServiceResult.Failure("New password must be different from the current password");
        }

        // Update password
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Password changed for user {UserId}", userId);

        return ServiceResult.Success("Password changed successfully");
    }

    public async Task<string?> GetResetTokenForTestingAsync(string email)
    {
        // This method is for testing purposes only - should not be exposed in production
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower());

        if (user == null) return null;

        // Generate new token without expiring old ones
        var token = GenerateResetToken();
        var tokenHash = HashToken(token);

        var resetToken = new PasswordResetToken
        {
            TokenHash = tokenHash,
            UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.AddHours(TokenExpirationHours),
            RequestedByIp = "127.0.0.1",
            CreatedAt = DateTime.UtcNow
        };

        _context.PasswordResetTokens.Add(resetToken);
        await _context.SaveChangesAsync();

        return token;
    }

    #region Private Methods

    private static string GenerateResetToken()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private static ServiceResult ValidatePasswordStrength(string password)
    {
        var errors = new List<string>();

        if (password.Length < 8)
        {
            errors.Add("Password must be at least 8 characters long");
        }

        if (!password.Any(char.IsUpper))
        {
            errors.Add("Password must contain at least one uppercase letter");
        }

        if (!password.Any(char.IsLower))
        {
            errors.Add("Password must contain at least one lowercase letter");
        }

        if (!password.Any(char.IsDigit))
        {
            errors.Add("Password must contain at least one number");
        }

        if (!password.Any(c => !char.IsLetterOrDigit(c)))
        {
            errors.Add("Password must contain at least one special character");
        }

        if (errors.Count != 0)
        {
            return ServiceResult.Failure(errors);
        }

        return ServiceResult.Success();
    }

    private static string GenerateResetEmailBody(string username, string token)
    {
        return $@"
Hello {username},

A password reset was requested for your ShopInventory account.

To reset your password, use the following link or enter the code below:

Reset Token: {token}

This link will expire in {TokenExpirationHours} hours.

If you did not request this password reset, please ignore this email or contact support if you have concerns.

Best regards,
ShopInventory Team
";
    }

    #endregion
}
