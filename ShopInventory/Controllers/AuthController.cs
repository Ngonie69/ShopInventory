using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Authentication controller for login, logout, and token management
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IAuditService _auditService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, IAuditService auditService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _auditService = auditService;
        _logger = logger;
    }

    /// <summary>
    /// Login with username and password
    /// </summary>
    /// <param name="request">Login credentials</param>
    /// <returns>JWT tokens</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] AuthLoginRequest request)
    {
        var ipAddress = GetClientIpAddress();

        // Check if IP is locked out
        if (_authService.IsLockedOut(ipAddress))
        {
            _logger.LogWarning("Login attempt from locked out IP: {IpAddress}", ipAddress);
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponseDto
            {
                Message = "Too many failed login attempts. Please try again later."
            });
        }

        var result = await _authService.AuthenticateAsync(request, ipAddress);

        if (result == null)
        {
            try { await _auditService.LogAsync(AuditActions.LoginFailed, request.Username, "Unknown", "User", null, $"Failed login attempt for {request.Username}", null, false, "Invalid username or password"); } catch { }
            return Unauthorized(new ErrorResponseDto
            {
                Message = "Invalid username or password"
            });
        }

        try { await _auditService.LogAsync(AuditActions.Login, result.User?.Username ?? request.Username, result.User?.Role ?? "Unknown", "User", null, $"User {result.User?.Username ?? request.Username} logged in"); } catch { }
        return Ok(result);
    }

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    /// <param name="request">Refresh token</param>
    /// <returns>New JWT tokens</returns>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        var ipAddress = GetClientIpAddress();
        var result = await _authService.RefreshTokenAsync(request.RefreshToken, ipAddress);

        if (result == null)
        {
            return Unauthorized(new ErrorResponseDto
            {
                Message = "Invalid or expired refresh token"
            });
        }

        try { await _auditService.LogAsync(AuditActions.RefreshToken, "User"); } catch { }
        return Ok(result);
    }

    /// <summary>
    /// Logout and revoke refresh token
    /// </summary>
    /// <param name="request">Refresh token to revoke</param>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request)
    {
        var ipAddress = GetClientIpAddress();
        await _authService.RevokeTokenAsync(request.RefreshToken, ipAddress);
        try { await _auditService.LogAsync(AuditActions.Logout, "User"); } catch { }
        return NoContent();
    }

    /// <summary>
    /// Get current user information
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser()
    {
        var username = User.Identity?.Name;

        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _authService.GetUserByUsernameAsync(username);
        if (user == null)
        {
            return Unauthorized();
        }

        return Ok(new UserInfo
        {
            Username = user.Username,
            Role = user.Role,
            Email = user.Email,
            AssignedWarehouseCode = user.AssignedWarehouseCode,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
            AssignedCustomerCodes = user.GetCustomerCodes()
        });
    }

    /// <summary>
    /// Register a new user (Admin only)
    /// </summary>
    /// <param name="request">User registration details</param>
    /// <returns>Created user information</returns>
    [HttpPost("register")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Register([FromBody] RegisterUserRequest request)
    {
        _logger.LogInformation("Admin {Admin} attempting to register new user: {Username}",
            User.Identity?.Name, request.Username);

        // Validate role
        var validRoles = new[] { "Admin", "Manager", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep" };
        if (!validRoles.Contains(request.Role, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = $"Invalid role. Valid roles are: {string.Join(", ", validRoles)}"
            });
        }

        var user = await _authService.RegisterUserAsync(
            request.Username,
            request.Email ?? string.Empty,
            request.Password,
            request.Role
        );

        if (user == null)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Username or email already exists"
            });
        }

        _logger.LogInformation("Admin {Admin} successfully registered new user: {Username} with role {Role}",
            User.Identity?.Name, user.Username, user.Role);

        try { await _auditService.LogAsync(AuditActions.RegisterUser, "User", user.Username, $"Registered user {user.Username} with role {user.Role}", true); } catch { }

        return CreatedAtAction(nameof(GetCurrentUser), new UserInfo
        {
            Username = user.Username,
            Role = user.Role,
            Email = user.Email
        });
    }

    private string GetClientIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
