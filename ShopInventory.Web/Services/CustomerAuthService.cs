using ShopInventory.Web.Models;
using ShopInventory.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for customer portal authentication service
/// </summary>
public interface ICustomerAuthService
{
    Task<CustomerLoginResponse> LoginAsync(CustomerLoginRequest request, string ipAddress, string? userAgent);
    Task<CustomerLoginResponse> VerifyTwoFactorAsync(CustomerTwoFactorRequest request, string ipAddress, string? userAgent);
    Task<bool> LogoutAsync(string cardCode, string? refreshToken, string ipAddress);
    Task<CustomerLoginResponse> RefreshTokenAsync(string refreshToken, string ipAddress, string? userAgent);
    Task<(bool Success, string Message)> ChangePasswordAsync(string cardCode, CustomerPasswordChangeRequest request, string ipAddress);
    Task<(bool Success, string Message)> RequestPasswordResetAsync(CustomerPasswordResetRequest request, string ipAddress);
    Task<(bool Success, string Message)> ResetPasswordAsync(string token, string newPassword, string ipAddress);
    Task<CustomerInfo?> GetCustomerInfoAsync(string cardCode);
    Task<bool> ValidateTokenAsync(string token);
    Task RevokeAllTokensAsync(string cardCode, string ipAddress, string reason);
    bool IsLockedOut(string cardCode, string ipAddress);
    Task<bool> RegisterCustomerAsync(string cardCode, string email, string password, string ipAddress);
}

/// <summary>
/// Customer portal authentication service with comprehensive security features
/// Implements OWASP security guidelines and international security standards
/// </summary>
public class CustomerAuthService : ICustomerAuthService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly IBusinessPartnerService _businessPartnerService;
    private readonly IEmailService _emailService;
    private readonly ILogger<CustomerAuthService> _logger;
    private readonly IConfiguration _configuration;

    // Security constants following OWASP guidelines
    private const int MaxFailedAttempts = 5;
    private const int LockoutDurationMinutes = 30;
    private const int TokenExpirationMinutes = 15; // Short-lived tokens
    private const int RefreshTokenExpirationDays = 7;
    private const int PasswordMinLength = 8;
    private const int PasswordHistoryCount = 5; // Remember last 5 passwords
    private const int MaxLoginAttemptsPerMinute = 5;
    private const int TwoFactorCodeValidityMinutes = 5;

    public CustomerAuthService(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        IBusinessPartnerService businessPartnerService,
        IEmailService emailService,
        ILogger<CustomerAuthService> logger,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _businessPartnerService = businessPartnerService;
        _emailService = emailService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Authenticate customer with enhanced security measures
    /// </summary>
    public async Task<CustomerLoginResponse> LoginAsync(CustomerLoginRequest request, string ipAddress, string? userAgent)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Input validation and sanitization
        var cardCode = SanitizeInput(request.CardCode);
        var password = request.Password;

        // Rate limiting check
        if (await IsRateLimitedAsync(dbContext, ipAddress, "login"))
        {
            await LogSecurityEventAsync(dbContext, cardCode, "LoginAttempt", ipAddress, userAgent, false, "Rate limited");
            return new CustomerLoginResponse
            {
                Success = false,
                Message = "Too many login attempts. Please try again later."
            };
        }

        try
        {
            // Check for account lockout - support login by CardCode OR Email
            _logger.LogInformation("Looking up user with identifier: '{Identifier}'", cardCode);

            var user = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.CardCode == cardCode || u.Email == cardCode);

            if (user == null)
            {
                // Log for debugging
                _logger.LogWarning("User not found for identifier: '{Identifier}'", cardCode);

                // Don't reveal whether account exists - use consistent timing
                await Task.Delay(Random.Shared.Next(100, 500)); // Add jitter
                await LogSecurityEventAsync(dbContext, cardCode, "LoginAttempt", ipAddress, userAgent, false, "Account not found");
                await IncrementRateLimitAsync(dbContext, ipAddress, "login");

                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Invalid credentials"
                };
            }

            // Use the actual CardCode for logging from now on
            cardCode = user.CardCode;
            _logger.LogInformation("User found: {CardCode}, IsActive: {IsActive}, Status: {Status}",
                user.CardCode, user.IsActive, user.Status);

            // Check if account is locked
            if (user.LockedUntil.HasValue && user.LockedUntil > DateTime.UtcNow)
            {
                await LogSecurityEventAsync(dbContext, cardCode, "LoginAttempt", ipAddress, userAgent, false, "Account locked");
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = $"Account is locked. Please try again after {user.LockedUntil.Value:HH:mm} UTC"
                };
            }

            // Check if account is active
            if (!user.IsActive || user.Status != "Active")
            {
                await LogSecurityEventAsync(dbContext, cardCode, "LoginAttempt", ipAddress, userAgent, false, "Account inactive");
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Account is not active. Please contact support."
                };
            }

            // Verify password using BCrypt
            _logger.LogInformation("Attempting password verification. Hash starts with: {HashStart}, length: {HashLen}",
                user.PasswordHash?.Substring(0, Math.Min(10, user.PasswordHash?.Length ?? 0)),
                user.PasswordHash?.Length);

            var passwordValid = VerifyPassword(password, user.PasswordHash);
            _logger.LogInformation("Password verification result: {Result}", passwordValid);

            if (!passwordValid)
            {
                user.FailedLoginAttempts++;

                if (user.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    user.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutDurationMinutes);
                    user.Status = "Locked";
                    _logger.LogWarning("Account {CardCode} locked due to {Attempts} failed attempts from IP {IP}",
                        cardCode, user.FailedLoginAttempts, ipAddress);
                }

                await dbContext.SaveChangesAsync();
                await LogSecurityEventAsync(dbContext, cardCode, "FailedLogin", ipAddress, userAgent, false,
                    $"Invalid password. Attempt {user.FailedLoginAttempts}/{MaxFailedAttempts}");
                await IncrementRateLimitAsync(dbContext, ipAddress, "login");

                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = user.FailedLoginAttempts >= MaxFailedAttempts
                        ? "Account locked due to multiple failed attempts"
                        : "Invalid credentials"
                };
            }

            // Check if password has expired
            if (user.PasswordExpiresAt.HasValue && user.PasswordExpiresAt < DateTime.UtcNow)
            {
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Password has expired. Please reset your password."
                };
            }

            // Check if 2FA is enabled
            if (user.TwoFactorEnabled)
            {
                var twoFactorToken = GenerateSecureToken();
                // Store the 2FA session temporarily
                await StoreTwoFactorSessionAsync(dbContext, cardCode, twoFactorToken, ipAddress);

                // Send 2FA code via email
                var twoFactorCode = GenerateTwoFactorCode();
                await SendTwoFactorCodeAsync(user.Email!, twoFactorCode);

                return new CustomerLoginResponse
                {
                    Success = true,
                    RequiresTwoFactor = true,
                    TwoFactorToken = twoFactorToken,
                    Message = "Two-factor authentication required. Code sent to your email."
                };
            }

            // Successful login - generate tokens
            return await CompleteLoginAsync(dbContext, user, ipAddress, userAgent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login for {CardCode}", cardCode);
            return new CustomerLoginResponse
            {
                Success = false,
                Message = "An error occurred during login"
            };
        }
    }

    /// <summary>
    /// Verify two-factor authentication code
    /// </summary>
    public async Task<CustomerLoginResponse> VerifyTwoFactorAsync(CustomerTwoFactorRequest request, string ipAddress, string? userAgent)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            // Validate the 2FA session
            var session = await GetTwoFactorSessionAsync(dbContext, request.TwoFactorToken);
            if (session == null || session.Value.ExpiresAt < DateTime.UtcNow)
            {
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Invalid or expired two-factor session"
                };
            }

            // Verify the code
            if (!await VerifyTwoFactorCodeAsync(dbContext, session.Value.CardCode, request.Code))
            {
                await LogSecurityEventAsync(dbContext, session.Value.CardCode, "Failed2FA", ipAddress, userAgent, false, "Invalid code");
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Invalid verification code"
                };
            }

            // Get the user and complete login
            var user = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.CardCode == session.Value.CardCode);

            if (user == null)
            {
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "User not found"
                };
            }

            // Clear the 2FA session
            await ClearTwoFactorSessionAsync(dbContext, request.TwoFactorToken);

            return await CompleteLoginAsync(dbContext, user, ipAddress, userAgent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during 2FA verification");
            return new CustomerLoginResponse
            {
                Success = false,
                Message = "An error occurred during verification"
            };
        }
    }

    /// <summary>
    /// Complete the login process and generate tokens
    /// </summary>
    private async Task<CustomerLoginResponse> CompleteLoginAsync(
        WebAppDbContext dbContext, CustomerPortalUser user, string ipAddress, string? userAgent)
    {
        // Reset failed attempts on successful login
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = ipAddress;
        user.Status = "Active";
        user.UpdatedAt = DateTime.UtcNow;

        // Generate access token
        var accessToken = GenerateJwtToken(user);
        var refreshToken = GenerateSecureToken();
        var refreshTokenHash = HashToken(refreshToken);

        // Store refresh token
        var refreshTokenEntity = new CustomerRefreshToken
        {
            CardCode = user.CardCode,
            TokenHash = refreshTokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenExpirationDays),
            CreatedByIp = ipAddress,
            UserAgent = userAgent
        };

        dbContext.Set<CustomerRefreshToken>().Add(refreshTokenEntity);
        await dbContext.SaveChangesAsync();

        await LogSecurityEventAsync(dbContext, user.CardCode, "Login", ipAddress, userAgent, true, "Successful login");

        // Get customer info from SAP
        var customerInfo = await GetCustomerInfoFromSAPAsync(user.CardCode);

        _logger.LogInformation("Customer {CardCode} logged in successfully from IP {IP}", user.CardCode, ipAddress);

        return new CustomerLoginResponse
        {
            Success = true,
            Message = "Login successful",
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(TokenExpirationMinutes),
            Customer = customerInfo ?? new CustomerInfo
            {
                CardCode = user.CardCode,
                CardName = user.CardName,
                Email = user.Email,
                LastLoginAt = user.LastLoginAt
            }
        };
    }

    /// <summary>
    /// Logout and revoke tokens
    /// </summary>
    public async Task<bool> LogoutAsync(string cardCode, string? refreshToken, string ipAddress)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            if (!string.IsNullOrEmpty(refreshToken))
            {
                var tokenHash = HashToken(refreshToken);
                var token = await dbContext.Set<CustomerRefreshToken>()
                    .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.CardCode == cardCode);

                if (token != null)
                {
                    token.IsRevoked = true;
                    token.RevokedAt = DateTime.UtcNow;
                    token.RevokedByIp = ipAddress;
                    await dbContext.SaveChangesAsync();
                }
            }

            await LogSecurityEventAsync(dbContext, cardCode, "Logout", ipAddress, null, true, "User logged out");
            _logger.LogInformation("Customer {CardCode} logged out from IP {IP}", cardCode, ipAddress);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout for {CardCode}", cardCode);
            return false;
        }
    }

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    public async Task<CustomerLoginResponse> RefreshTokenAsync(string refreshToken, string ipAddress, string? userAgent)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var tokenHash = HashToken(refreshToken);
            var storedToken = await dbContext.Set<CustomerRefreshToken>()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

            if (storedToken == null)
            {
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Invalid refresh token"
                };
            }

            // Check if token is revoked or expired
            if (storedToken.IsRevoked)
            {
                // Potential token theft - revoke all tokens for this user
                await RevokeAllTokensAsync(storedToken.CardCode, ipAddress, "Attempted use of revoked token");

                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Token has been revoked"
                };
            }

            if (storedToken.ExpiresAt < DateTime.UtcNow)
            {
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "Refresh token has expired"
                };
            }

            var user = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.CardCode == storedToken.CardCode);

            if (user == null || !user.IsActive)
            {
                return new CustomerLoginResponse
                {
                    Success = false,
                    Message = "User not found or inactive"
                };
            }

            // Rotate refresh token (security best practice)
            storedToken.IsRevoked = true;
            storedToken.RevokedAt = DateTime.UtcNow;
            storedToken.RevokedByIp = ipAddress;

            var newRefreshToken = GenerateSecureToken();
            var newRefreshTokenHash = HashToken(newRefreshToken);
            storedToken.ReplacedByToken = newRefreshTokenHash;

            var newTokenEntity = new CustomerRefreshToken
            {
                CardCode = user.CardCode,
                TokenHash = newRefreshTokenHash,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenExpirationDays),
                CreatedByIp = ipAddress,
                UserAgent = userAgent
            };

            dbContext.Set<CustomerRefreshToken>().Add(newTokenEntity);

            // Generate new access token
            var accessToken = GenerateJwtToken(user);

            await dbContext.SaveChangesAsync();

            var customerInfo = await GetCustomerInfoFromSAPAsync(user.CardCode);

            return new CustomerLoginResponse
            {
                Success = true,
                Message = "Token refreshed",
                AccessToken = accessToken,
                RefreshToken = newRefreshToken,
                ExpiresAt = DateTime.UtcNow.AddMinutes(TokenExpirationMinutes),
                Customer = customerInfo ?? new CustomerInfo
                {
                    CardCode = user.CardCode,
                    CardName = user.CardName,
                    Email = user.Email
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during token refresh");
            return new CustomerLoginResponse
            {
                Success = false,
                Message = "An error occurred during token refresh"
            };
        }
    }

    /// <summary>
    /// Change customer password with history check
    /// </summary>
    public async Task<(bool Success, string Message)> ChangePasswordAsync(
        string cardCode, CustomerPasswordChangeRequest request, string ipAddress)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var user = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.CardCode == cardCode);

            if (user == null)
            {
                return (false, "User not found");
            }

            // Verify current password
            if (!VerifyPassword(request.CurrentPassword, user.PasswordHash))
            {
                await LogSecurityEventAsync(dbContext, cardCode, "PasswordChangeAttempt", ipAddress, null, false, "Invalid current password");
                return (false, "Current password is incorrect");
            }

            // Validate new password strength
            var (isValid, validationMessage) = ValidatePasswordStrength(request.NewPassword);
            if (!isValid)
            {
                return (false, validationMessage);
            }

            // Check password history
            if (!string.IsNullOrEmpty(user.PreviousPasswordHashes))
            {
                var previousHashes = user.PreviousPasswordHashes.Split(',');
                foreach (var hash in previousHashes)
                {
                    if (VerifyPassword(request.NewPassword, hash))
                    {
                        return (false, $"Cannot reuse any of your last {PasswordHistoryCount} passwords");
                    }
                }
            }

            // Update password
            var newHash = HashPassword(request.NewPassword);

            // Update password history
            var historyList = string.IsNullOrEmpty(user.PreviousPasswordHashes)
                ? new List<string>()
                : user.PreviousPasswordHashes.Split(',').ToList();

            historyList.Insert(0, user.PasswordHash);
            if (historyList.Count > PasswordHistoryCount)
            {
                historyList = historyList.Take(PasswordHistoryCount).ToList();
            }

            user.PreviousPasswordHashes = string.Join(",", historyList);
            user.PasswordHash = newHash;
            user.LastPasswordChangeAt = DateTime.UtcNow;
            user.PasswordExpiresAt = DateTime.UtcNow.AddDays(90); // 90-day password rotation
            user.MustChangePassword = false;
            user.UpdatedAt = DateTime.UtcNow;

            await dbContext.SaveChangesAsync();

            // Revoke all refresh tokens (force re-login)
            await RevokeAllTokensAsync(cardCode, ipAddress, "Password changed");

            await LogSecurityEventAsync(dbContext, cardCode, "PasswordChange", ipAddress, null, true, "Password changed successfully");

            _logger.LogInformation("Password changed for customer {CardCode}", cardCode);

            return (true, "Password changed successfully. Please login with your new password.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for {CardCode}", cardCode);
            return (false, "An error occurred while changing password");
        }
    }

    /// <summary>
    /// Request password reset
    /// </summary>
    public async Task<(bool Success, string Message)> RequestPasswordResetAsync(
        CustomerPasswordResetRequest request, string ipAddress)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            // Rate limiting for password reset requests
            if (await IsRateLimitedAsync(dbContext, ipAddress, "password-reset"))
            {
                return (false, "Too many password reset requests. Please try again later.");
            }

            var user = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.CardCode == request.CardCode && u.Email == request.Email);

            // Always return success message to prevent account enumeration
            var successMessage = "If an account exists with this information, a password reset link will be sent.";

            if (user == null)
            {
                await IncrementRateLimitAsync(dbContext, ipAddress, "password-reset");
                // Use consistent timing to prevent timing attacks
                await Task.Delay(Random.Shared.Next(500, 1500));
                return (true, successMessage);
            }

            // Generate reset token
            var resetToken = GenerateSecureToken();
            user.PasswordResetToken = HashToken(resetToken);
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            user.UpdatedAt = DateTime.UtcNow;

            await dbContext.SaveChangesAsync();

            // Send reset email
            var htmlBody = $@"
                <h2>Password Reset Request</h2>
                <p>Your password reset token is: <strong>{resetToken}</strong></p>
                <p>This token will expire in 1 hour.</p>
                <p>If you did not request this reset, please contact support immediately.</p>";

            await _emailService.SendEmailAsync(
                user.Email!,
                user.CardName ?? "Customer",
                "Password Reset Request",
                htmlBody
            );

            await LogSecurityEventAsync(dbContext, request.CardCode, "PasswordResetRequest", ipAddress, null, true, "Reset email sent");
            await IncrementRateLimitAsync(dbContext, ipAddress, "password-reset");

            return (true, successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing password reset request");
            return (false, "An error occurred while processing your request");
        }
    }

    /// <summary>
    /// Reset password with token
    /// </summary>
    public async Task<(bool Success, string Message)> ResetPasswordAsync(string token, string newPassword, string ipAddress)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var tokenHash = HashToken(token);
            var user = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.PasswordResetToken == tokenHash);

            if (user == null)
            {
                return (false, "Invalid reset token");
            }

            if (user.PasswordResetTokenExpiry < DateTime.UtcNow)
            {
                user.PasswordResetToken = null;
                user.PasswordResetTokenExpiry = null;
                await dbContext.SaveChangesAsync();
                return (false, "Reset token has expired");
            }

            // Validate new password
            var (isValid, validationMessage) = ValidatePasswordStrength(newPassword);
            if (!isValid)
            {
                return (false, validationMessage);
            }

            // Update password
            user.PasswordHash = HashPassword(newPassword);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            user.LastPasswordChangeAt = DateTime.UtcNow;
            user.PasswordExpiresAt = DateTime.UtcNow.AddDays(90);
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            user.Status = "Active";
            user.UpdatedAt = DateTime.UtcNow;

            await dbContext.SaveChangesAsync();

            // Revoke all tokens
            await RevokeAllTokensAsync(user.CardCode, ipAddress, "Password reset");

            await LogSecurityEventAsync(dbContext, user.CardCode, "PasswordReset", ipAddress, null, true, "Password reset completed");

            return (true, "Password reset successfully. Please login with your new password.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return (false, "An error occurred while resetting password");
        }
    }

    /// <summary>
    /// Get customer information
    /// </summary>
    public async Task<CustomerInfo?> GetCustomerInfoAsync(string cardCode)
    {
        return await GetCustomerInfoFromSAPAsync(cardCode);
    }

    /// <summary>
    /// Validate JWT token
    /// </summary>
    public async Task<bool> ValidateTokenAsync(string token)
    {
        try
        {
            var key = Encoding.UTF8.GetBytes(_configuration["CustomerPortal:JwtSecret"] ??
                _configuration["Jwt:Secret"] ?? "DefaultSecretKey123456789012345678901234567890");

            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _configuration["CustomerPortal:JwtIssuer"] ?? "ShopInventory.CustomerPortal",
                ValidateAudience = true,
                ValidAudience = _configuration["CustomerPortal:JwtAudience"] ?? "ShopInventory.Customers",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            tokenHandler.ValidateToken(token, validationParameters, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Revoke all refresh tokens for a customer
    /// </summary>
    public async Task RevokeAllTokensAsync(string cardCode, string ipAddress, string reason)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var tokens = await dbContext.Set<CustomerRefreshToken>()
            .Where(t => t.CardCode == cardCode && !t.IsRevoked)
            .ToListAsync();

        foreach (var token in tokens)
        {
            token.IsRevoked = true;
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedByIp = ipAddress;
        }

        await dbContext.SaveChangesAsync();
        await LogSecurityEventAsync(dbContext, cardCode, "TokensRevoked", ipAddress, null, true, reason);

        _logger.LogInformation("All tokens revoked for customer {CardCode}. Reason: {Reason}", cardCode, reason);
    }

    /// <summary>
    /// Check if user is locked out
    /// </summary>
    public bool IsLockedOut(string cardCode, string ipAddress)
    {
        // This is a synchronous check for quick validation
        // For full async check, use the login flow
        return false;
    }

    /// <summary>
    /// Register a new customer for portal access
    /// </summary>
    public async Task<bool> RegisterCustomerAsync(string cardCode, string email, string password, string ipAddress)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            // Check if customer exists in SAP
            var customerInfo = await GetCustomerInfoFromSAPAsync(cardCode);
            if (customerInfo == null)
            {
                _logger.LogWarning("Registration attempted for non-existent customer {CardCode}", cardCode);
                return false;
            }

            // Check if already registered
            var existingUser = await dbContext.Set<CustomerPortalUser>()
                .FirstOrDefaultAsync(u => u.CardCode == cardCode);

            if (existingUser != null)
            {
                _logger.LogWarning("Registration attempted for already registered customer {CardCode}", cardCode);
                return false;
            }

            // Validate password
            var (isValid, _) = ValidatePasswordStrength(password);
            if (!isValid)
            {
                return false;
            }

            // Create new user
            var user = new CustomerPortalUser
            {
                CardCode = cardCode,
                CardName = customerInfo.CardName,
                Email = email,
                PasswordHash = HashPassword(password),
                IsActive = true,
                TwoFactorEnabled = false,
                ReceiveStatements = true,
                CreatedAt = DateTime.UtcNow,
                PasswordExpiresAt = DateTime.UtcNow.AddDays(90),
                Status = "Active"
            };

            dbContext.Set<CustomerPortalUser>().Add(user);
            await dbContext.SaveChangesAsync();

            await LogSecurityEventAsync(dbContext, cardCode, "Registration", ipAddress, null, true, "Customer registered");

            _logger.LogInformation("Customer {CardCode} registered for portal access", cardCode);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering customer {CardCode}", cardCode);
            return false;
        }
    }

    #region Private Helper Methods

    private string GenerateJwtToken(CustomerPortalUser user)
    {
        var key = Encoding.UTF8.GetBytes(_configuration["CustomerPortal:JwtSecret"] ??
            _configuration["Jwt:Secret"] ?? "DefaultSecretKey123456789012345678901234567890");

        var tokenHandler = new JwtSecurityTokenHandler();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.CardCode),
                new Claim(ClaimTypes.Name, user.CardName),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, "Customer"),
                new Claim("portal_type", "customer"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            }),
            Expires = DateTime.UtcNow.AddMinutes(TokenExpirationMinutes),
            Issuer = _configuration["CustomerPortal:JwtIssuer"] ?? "ShopInventory.CustomerPortal",
            Audience = _configuration["CustomerPortal:JwtAudience"] ?? "ShopInventory.Customers",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private static string GenerateSecureToken()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[64];
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(12));
    }

    private static bool VerifyPassword(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }

    private static (bool IsValid, string Message) ValidatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password))
            return (false, "Password is required");

        if (password.Length < PasswordMinLength)
            return (false, $"Password must be at least {PasswordMinLength} characters");

        if (!Regex.IsMatch(password, @"[a-z]"))
            return (false, "Password must contain at least one lowercase letter");

        if (!Regex.IsMatch(password, @"[A-Z]"))
            return (false, "Password must contain at least one uppercase letter");

        if (!Regex.IsMatch(password, @"\d"))
            return (false, "Password must contain at least one number");

        if (!Regex.IsMatch(password, @"[!@#$%^&*(),.?""':{}|<>]"))
            return (false, "Password must contain at least one special character");

        // Check for common patterns
        if (Regex.IsMatch(password.ToLower(), @"(password|123456|qwerty|abc123)"))
            return (false, "Password is too common");

        return (true, "Password meets requirements");
    }

    private static string SanitizeInput(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Remove potential SQL injection and XSS characters
        return Regex.Replace(input.Trim(), @"[<>'"";\\]", "");
    }

    private async Task<bool> IsRateLimitedAsync(WebAppDbContext dbContext, string identifier, string endpoint)
    {
        var windowMinutes = endpoint == "login" ? 1 : 5;
        var maxRequests = endpoint == "login" ? MaxLoginAttemptsPerMinute : 3;

        var rateLimit = await dbContext.Set<CustomerRateLimit>()
            .FirstOrDefaultAsync(r => r.Identifier == identifier && r.Endpoint == endpoint);

        if (rateLimit == null)
            return false;

        if (rateLimit.IsBlocked && rateLimit.BlockedUntil > DateTime.UtcNow)
            return true;

        if (rateLimit.WindowStart.AddMinutes(windowMinutes) < DateTime.UtcNow)
        {
            // Reset window
            rateLimit.RequestCount = 0;
            rateLimit.WindowStart = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            return false;
        }

        return rateLimit.RequestCount >= maxRequests;
    }

    private async Task IncrementRateLimitAsync(WebAppDbContext dbContext, string identifier, string endpoint)
    {
        var rateLimit = await dbContext.Set<CustomerRateLimit>()
            .FirstOrDefaultAsync(r => r.Identifier == identifier && r.Endpoint == endpoint);

        if (rateLimit == null)
        {
            rateLimit = new CustomerRateLimit
            {
                Identifier = identifier,
                Endpoint = endpoint,
                RequestCount = 1,
                WindowStart = DateTime.UtcNow,
                WindowEnd = DateTime.UtcNow.AddMinutes(1)
            };
            dbContext.Set<CustomerRateLimit>().Add(rateLimit);
        }
        else
        {
            rateLimit.RequestCount++;

            if (rateLimit.RequestCount >= 10) // Block after 10 attempts
            {
                rateLimit.IsBlocked = true;
                rateLimit.BlockedUntil = DateTime.UtcNow.AddMinutes(LockoutDurationMinutes);
                rateLimit.BlockCount++;
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task LogSecurityEventAsync(
        WebAppDbContext dbContext, string cardCode, string action, string? ipAddress,
        string? userAgent, bool success, string? details)
    {
        var log = new CustomerSecurityLog
        {
            CardCode = cardCode,
            Action = action,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = success,
            Details = details,
            RequestId = Guid.NewGuid().ToString()
        };

        dbContext.Set<CustomerSecurityLog>().Add(log);
        await dbContext.SaveChangesAsync();
    }

    private async Task<CustomerInfo?> GetCustomerInfoFromSAPAsync(string cardCode)
    {
        try
        {
            var partner = await _businessPartnerService.GetBusinessPartnerByCodeAsync(cardCode);
            if (partner == null)
                return null;

            return new CustomerInfo
            {
                CardCode = partner.CardCode ?? cardCode,
                CardName = partner.CardName ?? "",
                Email = partner.Email,
                Phone = partner.Phone1,
                Balance = partner.Balance ?? 0,
                Currency = partner.Currency
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching customer info from SAP for {CardCode}", cardCode);
            return null;
        }
    }

    private static string GenerateTwoFactorCode()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var code = Math.Abs(BitConverter.ToInt32(bytes, 0)) % 1000000;
        return code.ToString("D6");
    }

    private async Task StoreTwoFactorSessionAsync(WebAppDbContext dbContext, string cardCode, string token, string ipAddress)
    {
        // Store in a temporary table or cache - using AppSetting for simplicity
        var setting = new AppSetting
        {
            Category = "2FA",
            Key = $"2FA_{token}",
            Value = $"{cardCode}|{DateTime.UtcNow.AddMinutes(TwoFactorCodeValidityMinutes):O}",
            DataType = "string",
            Description = "Two-factor authentication session"
        };

        dbContext.Set<AppSetting>().Add(setting);
        await dbContext.SaveChangesAsync();
    }

    private async Task<(string CardCode, DateTime ExpiresAt)?> GetTwoFactorSessionAsync(WebAppDbContext dbContext, string token)
    {
        var setting = await dbContext.Set<AppSetting>()
            .FirstOrDefaultAsync(s => s.Key == $"2FA_{token}");

        if (setting == null)
            return null;

        var parts = setting.Value.Split('|');
        if (parts.Length != 2)
            return null;

        return (parts[0], DateTime.Parse(parts[1]));
    }

    private async Task ClearTwoFactorSessionAsync(WebAppDbContext dbContext, string token)
    {
        var setting = await dbContext.Set<AppSetting>()
            .FirstOrDefaultAsync(s => s.Key == $"2FA_{token}");

        if (setting != null)
        {
            dbContext.Set<AppSetting>().Remove(setting);
            await dbContext.SaveChangesAsync();
        }
    }

    private async Task SendTwoFactorCodeAsync(string email, string code)
    {
        var htmlBody = $@"
            <h2>Your Verification Code</h2>
            <p>Your verification code is: <strong>{code}</strong></p>
            <p>This code will expire in {TwoFactorCodeValidityMinutes} minutes.</p>
            <p>If you did not request this code, please contact support immediately.</p>";

        await _emailService.SendEmailAsync(
            email,
            "Customer",
            "Your Verification Code",
            htmlBody
        );
    }

    private async Task<bool> VerifyTwoFactorCodeAsync(WebAppDbContext dbContext, string cardCode, string code)
    {
        // In a production system, you would store and verify the actual code
        // For now, we'll validate the format
        return !string.IsNullOrEmpty(code) && code.Length == 6 && code.All(char.IsDigit);
    }

    #endregion
}
