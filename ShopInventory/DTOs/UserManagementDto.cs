namespace ShopInventory.DTOs;

#region User Management DTOs

/// <summary>
/// User list response
/// </summary>
public class UserListResponseDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<UserDto> Users { get; set; } = new();
}

/// <summary>
/// User DTO for display
/// </summary>
public class UserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool IsActive { get; set; }
    public bool EmailVerified { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Create user request
/// </summary>
public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Role { get; set; } = "User";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

/// <summary>
/// Update user request
/// </summary>
public class UpdateUserRequest
{
    public string? Email { get; set; }
    public string? Role { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>
/// Change password request (admin)
/// </summary>
public class AdminChangePasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Unlock user request
/// </summary>
public class UnlockUserRequest
{
    public bool ResetFailedAttempts { get; set; } = true;
}

/// <summary>
/// User created response
/// </summary>
public class UserCreatedResponseDto
{
    public string Message { get; set; } = "User created successfully";
    public UserDto? User { get; set; }
}

/// <summary>
/// User activity summary
/// </summary>
public class UserActivitySummaryDto
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime? LastLoginAt { get; set; }
    public int TotalLogins { get; set; }
    public int TotalActions { get; set; }
    public int InvoicesCreated { get; set; }
    public int PaymentsCreated { get; set; }
    public int TransfersCreated { get; set; }
    public DateTime? LastActionAt { get; set; }
    public List<RecentActivityDto> RecentActivity { get; set; } = new();
}

/// <summary>
/// Recent activity entry
/// </summary>
public class RecentActivityDto
{
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>
/// System activity dashboard
/// </summary>
public class SystemActivityDashboardDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int TotalLogins { get; set; }
    public int FailedLogins { get; set; }
    public int TotalActions { get; set; }
    public List<ActivityByUserDto> ActivityByUser { get; set; } = new();
    public List<ActivityByTypeDto> ActivityByType { get; set; } = new();
    public List<HourlyActivityDto> HourlyActivity { get; set; } = new();
}

/// <summary>
/// Activity grouped by user
/// </summary>
public class ActivityByUserDto
{
    public string Username { get; set; } = string.Empty;
    public int ActionCount { get; set; }
    public int LoginCount { get; set; }
    public DateTime? LastActive { get; set; }
}

/// <summary>
/// Activity grouped by type
/// </summary>
public class ActivityByTypeDto
{
    public string ActionType { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal PercentageOfTotal { get; set; }
}

/// <summary>
/// Hourly activity breakdown
/// </summary>
public class HourlyActivityDto
{
    public int Hour { get; set; }
    public int ActionCount { get; set; }
    public int LoginCount { get; set; }
}

#endregion
