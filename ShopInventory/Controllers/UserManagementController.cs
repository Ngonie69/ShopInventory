using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for user management operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class UserManagementController : ControllerBase
{
    private readonly IUserManagementService _userManagementService;
    private readonly ILogger<UserManagementController> _logger;

    public UserManagementController(
        IUserManagementService userManagementService,
        ILogger<UserManagementController> logger)
    {
        _userManagementService = userManagementService;
        _logger = logger;
    }

    /// <summary>
    /// Get all users with pagination and filtering
    /// </summary>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Page size (default: 10)</param>
    /// <param name="search">Search term for username, email, or name</param>
    /// <param name="role">Filter by role</param>
    /// <param name="isActive">Filter by active status</param>
    /// <returns>Paged list of users</returns>
    [HttpGet]
    [RequirePermission(Permission.ViewUsers)]
    [ProducesResponseType(typeof(PagedResult<UserDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] bool? isActive = null)
    {
        var result = await _userManagementService.GetUsersAsync(page, pageSize, search, role, isActive);
        return Ok(result);
    }

    /// <summary>
    /// Get a specific user by ID
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>User details</returns>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permission.ViewUsers)]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var user = await _userManagementService.GetUserByIdAsync(id);
        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }
        return Ok(user);
    }

    /// <summary>
    /// Create a new user
    /// </summary>
    /// <param name="request">User creation request</param>
    /// <returns>Created user</returns>
    [HttpPost]
    [RequirePermission(Permission.CreateUsers)]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDetailRequest request)
    {
        var result = await _userManagementService.CreateUserAsync(request);
        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.Message, errors = result.Errors });
        }

        return CreatedAtAction(nameof(GetUser), new { id = result.Data!.Id }, result.Data);
    }

    /// <summary>
    /// Update an existing user
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="request">Update request</param>
    /// <returns>Success status</returns>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserDetailRequest request)
    {
        var result = await _userManagementService.UpdateUserAsync(id, request);
        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.Message, errors = result.Errors });
        }

        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Deactivate a user
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permission.DeleteUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        var result = await _userManagementService.DeleteUserAsync(id);
        if (!result.IsSuccess)
        {
            return NotFound(new { message = result.Message });
        }

        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Get user permissions
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>User permissions</returns>
    [HttpGet("{id:guid}/permissions")]
    [RequirePermission(Permission.ViewUsers)]
    [ProducesResponseType(typeof(UserPermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserPermissions(Guid id)
    {
        var permissions = await _userManagementService.GetUserPermissionsAsync(id);
        if (permissions == null)
        {
            return NotFound(new { message = "User not found" });
        }

        return Ok(permissions);
    }

    /// <summary>
    /// Update user permissions
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="request">Permissions update request</param>
    /// <returns>Success status</returns>
    [HttpPut("{id:guid}/permissions")]
    [RequirePermission(Permission.ManageUserPermissions)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateUserPermissions(Guid id, [FromBody] UpdatePermissionsRequest request)
    {
        var result = await _userManagementService.UpdateUserPermissionsAsync(id, request);
        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.Message, errors = result.Errors });
        }

        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Get all available permissions
    /// </summary>
    /// <returns>All available permissions grouped by category</returns>
    [HttpGet("permissions/available")]
    [RequirePermission(Permission.ViewUsers)]
    [ProducesResponseType(typeof(AvailablePermissionsResponse), StatusCodes.Status200OK)]
    public IActionResult GetAvailablePermissions()
    {
        var permissions = _userManagementService.GetAvailablePermissions();
        return Ok(permissions);
    }

    /// <summary>
    /// Unlock a user account
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Success status</returns>
    [HttpPost("{id:guid}/unlock")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlockUser(Guid id)
    {
        var result = await _userManagementService.UnlockUserAsync(id);
        if (!result.IsSuccess)
        {
            return NotFound(new { message = result.Message });
        }

        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Reset user's two-factor authentication
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Success status</returns>
    [HttpPost("{id:guid}/reset-2fa")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetTwoFactor(Guid id)
    {
        var result = await _userManagementService.ResetTwoFactorAsync(id);
        if (!result.IsSuccess)
        {
            return NotFound(new { message = result.Message });
        }

        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Get current user's profile
    /// </summary>
    /// <returns>Current user details</returns>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var user = await _userManagementService.GetUserByIdAsync(userId.Value);
        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }

        return Ok(user);
    }

    /// <summary>
    /// Get current user's permissions
    /// </summary>
    /// <returns>Current user's effective permissions</returns>
    [HttpGet("me/permissions")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUserPermissions()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var permissions = await _userManagementService.GetEffectivePermissionsAsync(userId.Value);
        return Ok(permissions);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }
        return null;
    }
}
