using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Common.Extensions;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using System.Net;
using System.Net.Sockets;
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

    /// <summary>
    /// Complete login after successful 2FA verification. Exchanges the challenge token + TOTP code for a real JWT.
    /// </summary>
    Task<AuthLoginResponse?> CompleteTwoFactorLoginAsync(string challengeToken, string code, bool isBackupCode, string ipAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Complete login after a successful passkey assertion.
    /// </summary>
    Task<AuthLoginResponse?> CompletePasskeyLoginAsync(Guid userId, string ipAddress, CancellationToken cancellationToken);
}

/// <summary>
/// Authentication service implementation with PostgreSQL database storage
/// </summary>
public class AuthService : IAuthService
{
    private const string UnknownIpAddress = "unknown";

    private readonly ApplicationDbContext _dbContext;
    private readonly JwtSettings _jwtSettings;
    private readonly SecuritySettings _securitySettings;
    private readonly ILogger<AuthService> _logger;

    // Pre-built dictionary for O(1) API key lookups instead of O(n) linear scan
    private readonly Dictionary<string, ApiKeyConfig> _apiKeyLookup;

    // In-memory storage for IP-based lockout (could also be moved to Redis for distributed scenarios)
    private static readonly ConcurrentDictionary<string, FailedLoginInfo> _failedAttempts = new();

    private readonly ITwoFactorPendingStore _pendingStore;
    private readonly ITwoFactorService _twoFactorService;

    public AuthService(
        ApplicationDbContext dbContext,
        IOptions<JwtSettings> jwtSettings,
        IOptions<SecuritySettings> securitySettings,
        ILogger<AuthService> logger,
        ITwoFactorPendingStore pendingStore,
        ITwoFactorService twoFactorService)
    {
        _dbContext = dbContext;
        _jwtSettings = jwtSettings.Value;
        _securitySettings = securitySettings.Value;
        _logger = logger;
        _pendingStore = pendingStore;
        _twoFactorService = twoFactorService;

        // Build dictionary once at construction for fast API key validation
        _apiKeyLookup = _securitySettings.ApiKeys
            .Where(k => k.IsActive && !string.IsNullOrEmpty(k.Key))
            .ToDictionary(k => k.Key, k => k);
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
            .WhereUsernameMatches(request.Username)
            .FirstOrDefaultAsync();

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

        // If 2FA is enabled, issue a challenge token instead of a real JWT.
        if (user.TwoFactorEnabled)
        {
            await _dbContext.SaveChangesAsync();
            var challengeToken = _pendingStore.CreateChallenge(user.Id);
            _logger.LogInformation("User {Username} passed password check; 2FA challenge issued from IP: {IpAddress}",
                user.Username, ipAddress);
            return new AuthLoginResponse
            {
                RequiresTwoFactor = true,
                TwoFactorToken = challengeToken
            };
        }

        user.LastLoginAt = DateTime.UtcNow;
        return await IssueLoginResponseAsync(user, ipAddress, CancellationToken.None);
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
                Email = user.Email,
                AssignedWarehouseCode = user.AssignedWarehouseCode,
                AssignedWarehouseCodes = user.GetWarehouseCodes(),
                AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
                DefaultGLAccount = user.DefaultGLAccount,
                AllowedPaymentBusinessPartners = user.GetAllowedPaymentBusinessPartners(),
                AssignedCustomerCodes = user.GetCustomerCodes()
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
        if (await _dbContext.Users.WhereUsernameMatches(username).AnyAsync())
        {
            _logger.LogWarning("Registration attempt with existing username: {Username}", username);
            return null;
        }

        // Check if email already exists
        if (!string.IsNullOrEmpty(email) && await _dbContext.Users.WhereEmailMatches(email).AnyAsync())
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
            .WhereUsernameMatches(username)
            .FirstOrDefaultAsync();
    }

    public async Task<AuthLoginResponse?> CompleteTwoFactorLoginAsync(
        string challengeToken, string code, bool isBackupCode, string ipAddress, CancellationToken cancellationToken)
    {
        var userId = _pendingStore.ConsumeChallenge(challengeToken);
        if (userId is null)
        {
            _logger.LogWarning("Invalid or expired 2FA challenge token from IP: {IpAddress}", ipAddress);
            return null;
        }

        var result = await _twoFactorService.VerifyCodeAsync(userId.Value, code, isBackupCode);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed 2FA code verification for user {UserId} from IP: {IpAddress}", userId.Value, ipAddress);
            return null;
        }

        var user = await _dbContext.Users.FindAsync([userId.Value], cancellationToken);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("2FA completed for unknown/inactive user {UserId}", userId.Value);
            return null;
        }

        user.LastLoginAt = DateTime.UtcNow;
        return await IssueLoginResponseAsync(user, ipAddress, cancellationToken);
    }

    public async Task<AuthLoginResponse?> CompletePasskeyLoginAsync(Guid userId, string ipAddress, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FindAsync([userId], cancellationToken);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Passkey login completed for unknown/inactive user {UserId}", userId);
            return null;
        }

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            _logger.LogWarning("Passkey login attempted for locked user {UserId}", userId);
            return null;
        }

        ClearFailedAttempts(ipAddress);
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;

        _logger.LogInformation("User {Username} completed passkey login from IP: {IpAddress}", user.Username, ipAddress);
        return await IssueLoginResponseAsync(user, ipAddress, cancellationToken);
    }

    /// <summary>
    /// Maximum lifetime for API keys that have no explicit expiration set.
    /// </summary>
    private static readonly TimeSpan MaxApiKeyLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// Warn in logs when a key is within this many days of expiring.
    /// </summary>
    private const int ExpiryWarningDays = 14;

    public ApiKeyConfig? ValidateApiKey(string apiKey)
    {
        if (!_apiKeyLookup.TryGetValue(apiKey, out var keyConfig))
        {
            return null;
        }

        if (keyConfig.ExpiresAt.HasValue && keyConfig.ExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Expired API key used: {KeyName}", keyConfig.Name);
            return null;
        }

        // Reject keys without an explicit expiration — require rotation
        if (!keyConfig.ExpiresAt.HasValue)
        {
            _logger.LogWarning(
                "API key {KeyName} has no expiration date configured. " +
                "Set an ExpiresAt value (max {MaxDays} days) to enforce key rotation",
                keyConfig.Name, MaxApiKeyLifetime.TotalDays);
            return null;
        }

        // Warn when a key is approaching expiry so operators can rotate proactively
        var daysUntilExpiry = (keyConfig.ExpiresAt.Value - DateTime.UtcNow).TotalDays;
        if (daysUntilExpiry <= ExpiryWarningDays)
        {
            _logger.LogWarning(
                "API key {KeyName} expires in {Days:F0} day(s). Rotate the key soon",
                keyConfig.Name, daysUntilExpiry);
        }

        return keyConfig;
    }

    public bool IsLockedOut(string ipAddress)
    {
        if (!TryGetTrackedIpAddress(ipAddress, out var trackedIpAddress))
        {
            return false;
        }

        if (!_failedAttempts.TryGetValue(trackedIpAddress, out var info))
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
        if (!TryGetTrackedIpAddress(ipAddress, out var trackedIpAddress))
        {
            return;
        }

        var info = _failedAttempts.GetOrAdd(trackedIpAddress, _ => new FailedLoginInfo());

        info.AttemptCount++;
        info.LastAttempt = DateTime.UtcNow;

        if (info.AttemptCount >= _securitySettings.MaxFailedLoginAttempts)
        {
            info.LockoutEnd = DateTime.UtcNow.AddMinutes(_securitySettings.LockoutDurationMinutes);
            _logger.LogWarning("IP {IpAddress} locked out after {AttemptCount} failed attempts",
                trackedIpAddress, info.AttemptCount);
        }
    }

    public void ClearFailedAttempts(string ipAddress)
    {
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            _failedAttempts.TryRemove(ipAddress, out _);
        }

        if (TryGetTrackedIpAddress(ipAddress, out var trackedIpAddress))
        {
            _failedAttempts.TryRemove(trackedIpAddress, out _);
        }
    }

    private static bool TryGetTrackedIpAddress(string? ipAddress, out string trackedIpAddress)
    {
        trackedIpAddress = UnknownIpAddress;

        if (string.IsNullOrWhiteSpace(ipAddress) ||
            string.Equals(ipAddress, UnknownIpAddress, StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(ipAddress, out var parsedAddress))
        {
            return false;
        }

        parsedAddress = NormalizeIpAddress(parsedAddress);
        if (IsInternalOrReservedAddress(parsedAddress))
        {
            return false;
        }

        trackedIpAddress = parsedAddress.ToString();
        return true;
    }

    private static IPAddress NormalizeIpAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        var hasEmbeddedIpv4 = bytes.Take(12).All(static value => value == 0) ||
                              (bytes.Take(10).All(static value => value == 0) && bytes[10] == 0xFF && bytes[11] == 0xFF);

        return hasEmbeddedIpv4 ? new IPAddress(bytes[^4..]) : address;
    }

    private static bool IsInternalOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   address.IsIPv6Multicast ||
                   address.Equals(IPAddress.IPv6Loopback) ||
                   (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private string GenerateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(ClaimTypes.Email, user.Email ?? ""),
            new Claim(JwtRegisteredClaimNames.Sub, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        if (!string.IsNullOrEmpty(user.AssignedWarehouseCodes))
        {
            foreach (var wh in user.GetWarehouseCodes())
            {
                claims.Add(new Claim("warehouse", wh));
            }
        }

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<AuthLoginResponse> IssueLoginResponseAsync(User user, string ipAddress, CancellationToken cancellationToken)
    {
        var accessToken = GenerateAccessToken(user);
        var refreshTokenValue = GenerateRefreshTokenValue();
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);

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
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AuthLoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = expiresAt,
            User = new UserInfo
            {
                Username = user.Username,
                Role = user.Role,
                Email = user.Email,
                AssignedWarehouseCode = user.AssignedWarehouseCode,
                AssignedWarehouseCodes = user.GetWarehouseCodes(),
                AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
                DefaultGLAccount = user.DefaultGLAccount,
                AllowedPaymentBusinessPartners = user.GetAllowedPaymentBusinessPartners(),
                AssignedCustomerCodes = user.GetCustomerCodes()
            }
        };
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
