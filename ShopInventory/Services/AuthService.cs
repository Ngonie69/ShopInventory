using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace ShopInventory.Services;

/// <summary>
/// Interface for authentication service
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Authenticate user with username and password
    /// </summary>
    Task<AuthLoginResponse?> AuthenticateAsync(AuthLoginRequest request, string ipAddress);

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    Task<AuthLoginResponse?> RefreshTokenAsync(string refreshToken, string ipAddress);

    /// <summary>
    /// Revoke a refresh token
    /// </summary>
    Task RevokeTokenAsync(string refreshToken, string? ipAddress = null);

    /// <summary>
    /// Validate an API key
    /// </summary>
    ApiKeyConfig? ValidateApiKey(string apiKey);

    /// <summary>
    /// Check if an IP address is locked out
    /// </summary>
    bool IsLockedOut(string ipAddress);

    /// <summary>
    /// Record a failed login attempt
    /// </summary>
    void RecordFailedAttempt(string ipAddress);

    /// <summary>
    /// Clear failed attempts for an IP address
    /// </summary>
    void ClearFailedAttempts(string ipAddress);

    /// <summary>
    /// Register a new user
    /// </summary>
    Task<User?> RegisterUserAsync(string username, string email, string password, string role);

    /// <summary>
    /// Get user by username
    /// </summary>
    Task<User?> GetUserByUsernameAsync(string username);
}

/// <summary>
/// Authentication service implementation with PostgreSQL database storage
/// </summary>
public class AuthService : IAuthService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly JwtSettings _jwtSettings;
    private readonly SecuritySettings _securitySettings;
    private readonly ILogger<AuthService> _logger;

    // In-memory storage for IP-based lockout (could also be moved to Redis for distributed scenarios)
    private static readonly ConcurrentDictionary<string, FailedLoginInfo> _failedAttempts = new();

    public AuthService(
        ApplicationDbContext dbContext,
        IOptions<JwtSettings> jwtSettings,
        IOptions<SecuritySettings> securitySettings,
        ILogger<AuthService> logger)
    {
        _dbContext = dbContext;
        _jwtSettings = jwtSettings.Value;
        _securitySettings = securitySettings.Value;
        _logger = logger;
    }

    public async Task<AuthLoginResponse?> AuthenticateAsync(AuthLoginRequest request, string ipAddress)
    {
        // Check for IP-based lockout
        if (IsLockedOut(ipAddress))
        {
            _logger.LogWarning("Login attempt from locked out IP: {IpAddress}", ipAddress);
            return null;
        }

        // Find user in database (case-insensitive username)
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == request.Username.ToLower());

        if (user == null)
        {
            RecordFailedAttempt(ipAddress);
            _logger.LogWarning("Failed login attempt for unknown user: {Username} from IP: {IpAddress}",
                request.Username, ipAddress);
            await Task.Delay(Random.Shared.Next(100, 500)); // Timing attack mitigation
            return null;
        }

        // Check if user account is active
        if (!user.IsActive)
        {
            _logger.LogWarning("Login attempt for inactive user: {Username} from IP: {IpAddress}",
                request.Username, ipAddress);
            return null;
        }

        // Check if user account is locked
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            _logger.LogWarning("Login attempt for locked user: {Username} from IP: {IpAddress}",
                request.Username, ipAddress);
            return null;
        }

        // Verify password using BCrypt (timing-safe)
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            RecordFailedAttempt(ipAddress);

            // Also track failed attempts on the user record
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= _securitySettings.MaxFailedLoginAttempts)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(_securitySettings.LockoutDurationMinutes);
                _logger.LogWarning("User {Username} locked out after {AttemptCount} failed attempts",
                    user.Username, user.FailedLoginAttempts);
            }
            await _dbContext.SaveChangesAsync();

            _logger.LogWarning("Failed login attempt with wrong password for user: {Username} from IP: {IpAddress}",
                request.Username, ipAddress);
            await Task.Delay(Random.Shared.Next(100, 500)); // Additional timing attack mitigation
            return null;
        }

        // Clear failed attempts on successful login
        ClearFailedAttempts(ipAddress);
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;

        // Generate tokens
        var accessToken = GenerateAccessToken(user);
        var refreshTokenValue = GenerateRefreshTokenValue();
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);

        // Store refresh token in database
        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshTokenValue,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            CreatedByIp = ipAddress
        };

        _dbContext.RefreshTokens.Add(refreshToken);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {Username} logged in successfully from IP: {IpAddress}",
            user.Username, ipAddress);

        return new AuthLoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = expiresAt,
            User = new UserInfo
            {
                Username = user.Username,
                Role = user.Role,
                Email = user.Email
            }
        };
    }

    public async Task<AuthLoginResponse?> RefreshTokenAsync(string refreshToken, string ipAddress)
    {
        // Find the refresh token in database
        var token = await _dbContext.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (token == null)
        {
            _logger.LogWarning("Invalid refresh token attempt from IP: {IpAddress}", ipAddress);
            return null;
        }

        if (!token.IsActive)
        {
            _logger.LogWarning("Inactive refresh token used from IP: {IpAddress}. Expired: {IsExpired}, Revoked: {IsRevoked}",
                ipAddress, token.IsExpired, token.IsRevoked);
            return null;
        }

        var user = token.User;
        if (user == null || !user.IsActive)
        {
            _logger.LogWarning("Refresh token for inactive/deleted user from IP: {IpAddress}", ipAddress);
            return null;
        }

        // Revoke old refresh token (rotation)
        token.IsRevoked = true;
        token.RevokedAt = DateTime.UtcNow;
        token.RevokedByIp = ipAddress;

        // Generate new tokens
        var newAccessToken = GenerateAccessToken(user);
        var newRefreshTokenValue = GenerateRefreshTokenValue();
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);

        // Link old token to new one
        token.ReplacedByToken = newRefreshTokenValue;

        // Store new refresh token in database
        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = newRefreshTokenValue,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            CreatedByIp = ipAddress
        };

        _dbContext.RefreshTokens.Add(newRefreshToken);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Tokens refreshed for user {Username} from IP: {IpAddress}",
            user.Username, ipAddress);

        return new AuthLoginResponse
        {
            AccessToken = newAccessToken,
            RefreshToken = newRefreshTokenValue,
            ExpiresAt = expiresAt,
            User = new UserInfo
            {
                Username = user.Username,
                Role = user.Role,
                Email = user.Email
            }
        };
    }

    public async Task RevokeTokenAsync(string refreshToken, string? ipAddress = null)
    {
        var token = await _dbContext.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (token != null && token.IsActive)
        {
            token.IsRevoked = true;
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedByIp = ipAddress;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Refresh token revoked for user: {Username}", token.User?.Username);
        }
    }

    public async Task<User?> RegisterUserAsync(string username, string email, string password, string role)
    {
        // Check if username already exists
        if (await _dbContext.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
        {
            _logger.LogWarning("Registration attempt with existing username: {Username}", username);
            return null;
        }

        // Check if email already exists
        if (!string.IsNullOrEmpty(email) && await _dbContext.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower()))
        {
            _logger.LogWarning("Registration attempt with existing email: {Email}", email);
            return null;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            PasswordHash = HashPassword(password),
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("New user registered: {Username} with role {Role}", username, role);
        return user;
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        return await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
    }

    public ApiKeyConfig? ValidateApiKey(string apiKey)
    {
        var keyConfig = _securitySettings.ApiKeys
            .FirstOrDefault(k => k.Key == apiKey && k.IsActive);

        if (keyConfig == null)
        {
            return null;
        }

        if (keyConfig.ExpiresAt.HasValue && keyConfig.ExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Expired API key used: {KeyName}", keyConfig.Name);
            return null;
        }

        return keyConfig;
    }

    public bool IsLockedOut(string ipAddress)
    {
        if (!_failedAttempts.TryGetValue(ipAddress, out var info))
        {
            return false;
        }

        if (info.LockoutEnd.HasValue && info.LockoutEnd.Value > DateTime.UtcNow)
        {
            return true;
        }

        // Reset if lockout has expired
        if (info.LockoutEnd.HasValue && info.LockoutEnd.Value <= DateTime.UtcNow)
        {
            _failedAttempts.TryRemove(ipAddress, out _);
        }

        return false;
    }

    public void RecordFailedAttempt(string ipAddress)
    {
        var info = _failedAttempts.GetOrAdd(ipAddress, _ => new FailedLoginInfo());

        info.AttemptCount++;
        info.LastAttempt = DateTime.UtcNow;

        if (info.AttemptCount >= _securitySettings.MaxFailedLoginAttempts)
        {
            info.LockoutEnd = DateTime.UtcNow.AddMinutes(_securitySettings.LockoutDurationMinutes);
            _logger.LogWarning("IP {IpAddress} locked out after {AttemptCount} failed attempts",
                ipAddress, info.AttemptCount);
        }
    }

    public void ClearFailedAttempts(string ipAddress)
    {
        _failedAttempts.TryRemove(ipAddress, out _);
    }

    private string GenerateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(ClaimTypes.Email, user.Email ?? ""),
            new Claim(JwtRegisteredClaimNames.Sub, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRefreshTokenValue()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Hash a password using BCrypt with configurable work factor.
    /// BCrypt automatically generates and includes a cryptographically secure salt.
    /// Work factor 12 means 2^12 = 4096 iterations, balancing security and performance.
    /// </summary>
    public static string HashPassword(string password, int workFactor = 12)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor);
    }

    /// <summary>
    /// Verify a password against a BCrypt hash.
    /// This method is timing-safe to prevent timing attacks.
    /// </summary>
    public static bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }

    private class FailedLoginInfo
    {
        public int AttemptCount { get; set; }
        public DateTime LastAttempt { get; set; }
        public DateTime? LockoutEnd { get; set; }
    }
}
