using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.DTOs;
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
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
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
            return Unauthorized(new ErrorResponseDto
            {
                Message = "Invalid username or password"
            });
        }

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
        return NoContent();
    }

    /// <summary>
    /// Get current user information
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetCurrentUser()
    {
        var username = User.Identity?.Name;
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;

        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        return Ok(new UserInfo
        {
            Username = username,
            Role = role ?? "User"
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
        var validRoles = new[] { "Admin", "User", "Manager" };
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

        return CreatedAtAction(nameof(GetCurrentUser), new UserInfo
        {
            Username = user.Username,
            Role = user.Role,
            Email = user.Email
        });
    }

    private string GetClientIpAddress()
    {
        // Check for forwarded IP (when behind proxy/load balancer)
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
