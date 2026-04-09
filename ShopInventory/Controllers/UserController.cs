using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using BC = BCrypt.Net.BCrypt;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for user management (Admin only)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class UserController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<UserController> _logger;

    public UserController(ApplicationDbContext context, ILogger<UserController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets all users (paginated)
    /// </summary>
    /// <param name="page">Page number (default 1)</param>
    /// <param name="pageSize">Page size (default 20)</param>
    /// <param name="search">Search by username or email</param>
    /// <param name="role">Filter by role</param>
    [HttpGet]
    [ProducesResponseType(typeof(UserListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Users.AsQueryable();

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(u => u.Username.Contains(search) || (u.Email != null && u.Email.Contains(search)));
        }

        if (!string.IsNullOrEmpty(role))
        {
            query = query.Where(u => u.Role == role);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var users = await query
            .OrderBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Role = u.Role,
                FirstName = u.FirstName,
                LastName = u.LastName,
                IsActive = u.IsActive,
                EmailVerified = u.EmailVerified,
                FailedLoginAttempts = u.FailedLoginAttempts,
                LockoutEnd = u.LockoutEnd,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
                AssignedWarehouseCodes = u.GetWarehouseCodes(),
                AssignedSection = u.AssignedSection
            })
            .ToListAsync(cancellationToken);

        return Ok(new UserListResponseDto
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Users = users
        });
    }

    /// <summary>
    /// Gets a user by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role,
            FirstName = user.FirstName,
            LastName = user.LastName,
            IsActive = user.IsActive,
            EmailVerified = user.EmailVerified,
            FailedLoginAttempts = user.FailedLoginAttempts,
            LockoutEnd = user.LockoutEnd,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AssignedCustomerCodes = user.GetCustomerCodes(),
            AssignedSection = user.AssignedSection
        });
    }

    /// <summary>
    /// Creates a new user
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(UserCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        // Validate request
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new ErrorResponseDto { Message = "Username and password are required" });
        }

        // Check for existing username
        if (await _context.Users.AnyAsync(u => u.Username == request.Username, cancellationToken))
        {
            return BadRequest(new ErrorResponseDto { Message = "Username already exists" });
        }

        // Check for existing email
        if (!string.IsNullOrEmpty(request.Email) && await _context.Users.AnyAsync(u => u.Email == request.Email, cancellationToken))
        {
            return BadRequest(new ErrorResponseDto { Message = "Email already exists" });
        }

        // Validate role
        var validRoles = new[] { "Admin", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep" };
        if (!validRoles.Contains(request.Role))
        {
            return BadRequest(new ErrorResponseDto { Message = $"Invalid role. Valid roles are: {string.Join(", ", validRoles)}" });
        }

        // Validate warehouse assignment for StockController/DepotController
        if ((request.Role == "StockController" || request.Role == "DepotController") && (request.AssignedWarehouseCodes == null || request.AssignedWarehouseCodes.Count == 0))
        {
            return BadRequest(new ErrorResponseDto { Message = $"At least one assigned warehouse code is required for {request.Role} role" });
        }

        // Validate customer assignment for Merchandiser
        if (request.Role == "Merchandiser" && (request.AssignedCustomerCodes == null || request.AssignedCustomerCodes.Count == 0))
        {
            return BadRequest(new ErrorResponseDto { Message = "At least one assigned customer code is required for Merchandiser role" });
        }

        // Validate section assignment for Driver
        if (request.Role == "Driver" && string.IsNullOrWhiteSpace(request.AssignedSection))
        {
            return BadRequest(new ErrorResponseDto { Message = "An assigned section is required for Driver role" });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = BC.HashPassword(request.Password),
            Email = request.Email,
            Role = request.Role,
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        if (request.Role == "StockController" || request.Role == "DepotController")
            user.SetWarehouseCodes(request.AssignedWarehouseCodes);

        if (request.Role == "Merchandiser")
            user.SetCustomerCodes(request.AssignedCustomerCodes);

        if (request.Role == "Driver")
            user.AssignedSection = request.AssignedSection;

        _context.Users.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} created by admin", user.Username);

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, new UserCreatedResponseDto
        {
            Message = "User created successfully",
            User = new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role,
                FirstName = user.FirstName,
                LastName = user.LastName,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                AssignedWarehouseCodes = user.GetWarehouseCodes(),
                AssignedCustomerCodes = user.GetCustomerCodes(),
                AssignedSection = user.AssignedSection
            }
        });
    }

    /// <summary>
    /// Updates a user
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        // Check for existing email
        if (!string.IsNullOrEmpty(request.Email) && request.Email != user.Email)
        {
            if (await _context.Users.AnyAsync(u => u.Email == request.Email && u.Id != id, cancellationToken))
            {
                return BadRequest(new ErrorResponseDto { Message = "Email already exists" });
            }
            user.Email = request.Email;
        }

        // Validate role
        if (!string.IsNullOrEmpty(request.Role))
        {
            var validRoles = new[] { "Admin", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep" };
            if (!validRoles.Contains(request.Role))
            {
                return BadRequest(new ErrorResponseDto { Message = $"Invalid role. Valid roles are: {string.Join(", ", validRoles)}" });
            }
            user.Role = request.Role;
        }

        if (request.FirstName != null) user.FirstName = request.FirstName;
        if (request.LastName != null) user.LastName = request.LastName;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;

        // Update assigned warehouses
        if (request.AssignedWarehouseCodes != null)
        {
            if (user.Role == "StockController" || user.Role == "DepotController")
                user.SetWarehouseCodes(request.AssignedWarehouseCodes);
            else
                user.SetWarehouseCodes(null);
        }

        // Update assigned customers
        if (request.AssignedCustomerCodes != null)
        {
            if (user.Role == "Merchandiser")
                user.SetCustomerCodes(request.AssignedCustomerCodes);
            else
                user.SetCustomerCodes(null);
        }

        // Update assigned section
        if (user.Role == "Driver")
            user.AssignedSection = request.AssignedSection;
        else
            user.AssignedSection = null;

        // Validate warehouses are set for warehouse-dependent roles
        if ((user.Role == "StockController" || user.Role == "DepotController") && user.GetWarehouseCodes().Count == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = $"At least one assigned warehouse code is required for {user.Role} role" });
        }

        // Validate customers are set for Merchandiser
        if (user.Role == "Merchandiser" && user.GetCustomerCodes().Count == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = "At least one assigned customer code is required for Merchandiser role" });
        }

        // Validate section is set for Driver
        if (user.Role == "Driver" && string.IsNullOrWhiteSpace(user.AssignedSection))
        {
            return BadRequest(new ErrorResponseDto { Message = "An assigned section is required for Driver role" });
        }

        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} updated by admin", user.Username);

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role,
            FirstName = user.FirstName,
            LastName = user.LastName,
            IsActive = user.IsActive,
            EmailVerified = user.EmailVerified,
            FailedLoginAttempts = user.FailedLoginAttempts,
            LockoutEnd = user.LockoutEnd,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AssignedCustomerCodes = user.GetCustomerCodes(),
            AssignedSection = user.AssignedSection
        });
    }

    /// <summary>
    /// Deletes a user
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        // Prevent deleting the last admin
        if (user.Role == "Admin")
        {
            var adminCount = await _context.Users.CountAsync(u => u.Role == "Admin", cancellationToken);
            if (adminCount <= 1)
            {
                return BadRequest(new ErrorResponseDto { Message = "Cannot delete the last admin user" });
            }
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} deleted by admin", user.Username);

        return NoContent();
    }

    /// <summary>
    /// Changes a user's password (admin action)
    /// </summary>
    [HttpPost("{id}/change-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(Guid id, [FromBody] AdminChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new ErrorResponseDto { Message = "Password must be at least 8 characters" });
        }

        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        user.PasswordHash = BC.HashPassword(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Password changed for user {Username} by admin", user.Username);

        return Ok(new { Message = "Password changed successfully" });
    }

    /// <summary>
    /// Unlocks a locked user account
    /// </summary>
    [HttpPost("{id}/unlock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlockUser(Guid id, [FromBody] UnlockUserRequest? request, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        user.LockoutEnd = null;
        if (request?.ResetFailedAttempts ?? true)
        {
            user.FailedLoginAttempts = 0;
        }
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} unlocked by admin", user.Username);

        return Ok(new { Message = "User account unlocked successfully" });
    }

    /// <summary>
    /// Deactivates a user account
    /// </summary>
    [HttpPost("{id}/deactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateUser(Guid id, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} deactivated by admin", user.Username);

        return Ok(new { Message = "User account deactivated successfully" });
    }

    /// <summary>
    /// Activates a user account
    /// </summary>
    [HttpPost("{id}/activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateUser(Guid id, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

        if (user == null)
        {
            return NotFound(new ErrorResponseDto { Message = "User not found" });
        }

        user.IsActive = true;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} activated by admin", user.Username);

        return Ok(new { Message = "User account activated successfully" });
    }

    /// <summary>
    /// Gets available roles
    /// </summary>
    [HttpGet("roles")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public IActionResult GetRoles()
    {
        var roles = new[] { "Admin", "Cashier", "StockController", "DepotController", "PodOperator" };
        return Ok(roles);
    }
}
