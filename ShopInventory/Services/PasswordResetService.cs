using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Password;
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
    /// Get user ID by username
    /// </summary>
    Task<Guid?> GetUserIdByUsernameAsync(string username);

    /// <summary>
    /// Update login credentials (username/email) for logged-in user
    /// </summary>
    Task<ServiceResult<UpdateCredentialsResponse>> UpdateCredentialsAsync(Guid userId, UpdateCredentialsRequest request);

    /// <summary>
    /// Get current credentials for logged-in user
    /// </summary>
    Task<UpdateCredentialsResponse?> GetCredentialsAsync(Guid userId);

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
    private readonly UserPasswords _passwords;
    private const int TokenExpirationHours = 24;
    private const int MaxResetAttemptsPerHour = 5;

    public PasswordResetService(
        ApplicationDbContext context,
        ILogger<PasswordResetService> logger,
        UserPasswords passwords,
        IEmailQueueService? emailService = null)
    {
        _context = context;
        _logger = logger;
        _passwords = passwords;
        _emailService = emailService;
    }

    public async Task<ServiceResult> InitiateResetAsync(string email, string ipAddress)
    {
        var normalizedEmail = email.Trim();

        // Rate limiting check
        var recentAttempts = await _context.PasswordResetTokens
            .AsNoTracking()
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
            .AsNoTracking()
            .WhereEmailMatches(normalizedEmail)
            .FirstOrDefaultAsync();

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
            .AsNoTracking()
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

        var user = resetToken.User;
        if (user is null)
        {
            return ServiceResult.Failure("User not found");
        }

        var validationResult = await ValidateNewPasswordAsync(user, newPassword);
        if (!validationResult.IsSuccess)
        {
            return validationResult;
        }

        // Sets the password, remembers the old one, clears any lockout and ends every session.
        await _passwords.ChangeAsync(user, newPassword, chosenByOwner: true, "Password reset", CancellationToken.None);

        // Mark token as used
        resetToken.IsUsed = true;
        resetToken.UsedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Password reset completed for user {UserId}", user.Id);

        return ServiceResult.Success("Password has been reset successfully. You can now log in with your new password.");
    }

    public async Task<ServiceResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            return ServiceResult.Failure("Current password is incorrect");
        }

        var validationResult = await ValidateNewPasswordAsync(user, newPassword);
        if (!validationResult.IsSuccess)
        {
            return validationResult;
        }

        // Sets the password, remembers the old one, clears the "must change" flag and revokes every
        // refresh token, so each device has to sign in again with the new password.
        await _passwords.ChangeAsync(user, newPassword, chosenByOwner: true, "Password changed", CancellationToken.None);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Password changed for user {UserId}", userId);

        return ServiceResult.Success("Password changed successfully");
    }

    public async Task<Guid?> GetUserIdByUsernameAsync(string username)
    {
        var normalizedUsername = username.Trim();

        var user = await _context.Users.AsNoTracking()
            .WhereUsernameMatches(normalizedUsername)
            .FirstOrDefaultAsync();
        return user?.Id;
    }

    public async Task<UpdateCredentialsResponse?> GetCredentialsAsync(Guid userId)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return null;

        var standing = _passwords.StandingOf(user);

        return new UpdateCredentialsResponse
        {
            Username = user.Username,
            Email = user.Email,
            Message = "OK",
            PasswordChangedAt = standing.ChangedAtUtc,
            PasswordExpiresAt = standing.ExpiresAtUtc,
            PasswordExpiresInDays = standing.ExpiresInDays,
            PasswordChangeRequired = standing.ChangeRequired,
            PasswordChangeReason = standing.Reason,
            PasswordExpiringSoon = _passwords.IsExpiringSoon(standing)
        };
    }

    public async Task<ServiceResult<UpdateCredentialsResponse>> UpdateCredentialsAsync(Guid userId, UpdateCredentialsRequest request)
    {
        // Load with AsNoTracking first just to verify/read current values, then use ExecuteUpdate for the actual write
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            _logger.LogWarning("UpdateCredentialsAsync: user {UserId} not found", userId);
            return ServiceResult<UpdateCredentialsResponse>.Failure("User not found");
        }

        var previousUsername = user.Username;
        var previousEmail = user.Email;
        var requestedUsername = string.IsNullOrWhiteSpace(request.Username)
            ? null
            : request.Username.Trim();
        var requestedEmail = request.Email == null
            ? null
            : request.Email.Trim();

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return ServiceResult<UpdateCredentialsResponse>.Failure("Current password is incorrect");
        }

        var usernameChanged = !string.IsNullOrWhiteSpace(requestedUsername) &&
            !string.Equals(requestedUsername, user.Username, StringComparison.OrdinalIgnoreCase);
        var emailChanged = request.Email != null &&
            !string.Equals(requestedEmail, user.Email, StringComparison.OrdinalIgnoreCase);

        if (!usernameChanged && !emailChanged)
        {
            return ServiceResult<UpdateCredentialsResponse>.Failure("No username or email change was submitted");
        }

        // Check for conflicts before writing
        if (usernameChanged)
        {
            var usernameTaken = await _context.Users
                .Where(u => u.Id != userId)
                .WhereUsernameOrEmailMatches(requestedUsername!)
                .AnyAsync();
            if (usernameTaken)
            {
                return ServiceResult<UpdateCredentialsResponse>.Failure("Username is already taken");
            }
        }

        if (emailChanged && !string.IsNullOrWhiteSpace(requestedEmail))
        {
            var emailTaken = await _context.Users
                .Where(u => u.Id != userId)
                .WhereUsernameOrEmailMatches(requestedEmail)
                .AnyAsync();
            if (emailTaken)
            {
                return ServiceResult<UpdateCredentialsResponse>.Failure("Email is already in use");
            }
        }

        var now = DateTime.UtcNow;
        var newUsername = usernameChanged ? requestedUsername! : user.Username;
        var newEmail = emailChanged
            ? (string.IsNullOrWhiteSpace(requestedEmail) ? null : requestedEmail)
            : user.Email;

        // Use ExecuteUpdateAsync for a direct SQL UPDATE — bypasses EF change tracking entirely
        var rowsAffected = await _context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Username, newUsername)
                .SetProperty(x => x.Email, newEmail)
                .SetProperty(x => x.UpdatedAt, now));

        _logger.LogInformation(
            "Credentials ExecuteUpdate for user {UserId}: {RowsAffected} rows affected. {OldUsername} -> {NewUsername}, email {OldEmail} -> {NewEmail}",
            userId, rowsAffected, previousUsername, newUsername, previousEmail, newEmail);

        if (rowsAffected == 0)
        {
            _logger.LogError("Credentials update wrote 0 rows for user {UserId} — possible DB connection issue", userId);
            return ServiceResult<UpdateCredentialsResponse>.Failure("Update failed: no rows were modified. Please try again.");
        }

        return ServiceResult<UpdateCredentialsResponse>.Success(new UpdateCredentialsResponse
        {
            Username = newUsername,
            Email = newEmail,
            Message = $"Credentials updated successfully. Your login username is '{newUsername}'."
        }, $"Credentials updated successfully. Your login username is '{newUsername}'.");
    }

    public async Task<string?> GetResetTokenForTestingAsync(string email)
    {
        var normalizedEmail = email.Trim();

        // This method is for testing purposes only - should not be exposed in production
        var user = await _context.Users
            .WhereEmailMatches(normalizedEmail)
            .FirstOrDefaultAsync();

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

    /// <summary>
    /// Everything a password chosen by its owner has to satisfy.
    /// </summary>
    /// <remarks>
    /// The character mix was already asked for on these two routes and stays. The company policy
    /// — length, nothing personal, nothing common, nothing this account used recently — is what
    /// every route asks, and comes from <see cref="UserPasswords"/>. All of it is reported as one
    /// message, because the callers of this service show <see cref="ServiceResult.Message"/> and
    /// nothing else.
    /// </remarks>
    private async Task<ServiceResult> ValidateNewPasswordAsync(User user, string newPassword)
    {
        var broken = new List<string>(ValidateCharacterMix(newPassword ?? string.Empty));
        broken.AddRange(await _passwords.CheckForChangeAsync(user, newPassword, CancellationToken.None));

        return broken.Count == 0
            ? ServiceResult.Success()
            : ServiceResult.Failure(string.Join(" ", broken));
    }

    private static List<string> ValidateCharacterMix(string password)
    {
        var errors = new List<string>();

        if (!password.Any(char.IsUpper))
        {
            errors.Add("Password must contain at least one uppercase letter.");
        }

        if (!password.Any(char.IsLower))
        {
            errors.Add("Password must contain at least one lowercase letter.");
        }

        if (!password.Any(char.IsDigit))
        {
            errors.Add("Password must contain at least one number.");
        }

        if (!password.Any(c => !char.IsLetterOrDigit(c)))
        {
            errors.Add("Password must contain at least one special character.");
        }

        return errors;
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
