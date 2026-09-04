using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.UserManagement.Queries.GetUsers;
using ShopInventory.Features.UserManagement.Queries.GetUser;
using ShopInventory.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;
using ShopInventory.Features.UserManagement.Queries.GetUserPermissions;
using ShopInventory.Features.UserManagement.Queries.GetAvailablePermissions;
using ShopInventory.Features.UserManagement.Queries.GetCurrentUser;
using ShopInventory.Features.UserManagement.Queries.GetCurrentUserPermissions;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateGlobalDriverAssignedCustomers;
using ShopInventory.Features.UserManagement.Commands.UpdateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;
using ShopInventory.Features.UserManagement.Commands.DeleteUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUserPermissions;
using ShopInventory.Features.UserManagement.Commands.UnlockUser;
using ShopInventory.Features.UserManagement.Commands.ResetTwoFactor;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class UserManagementController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// List users with full details
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetUsersQuery(page, pageSize, search, role, isActive), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get user with permissions
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetUserQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Create user with granular permissions
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.CreateUsers, Permission.CreateMerchandiserAccounts)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDetailRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateUserCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetUser), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// The merchandiser accounts the caller manages
    /// </summary>
    [HttpGet("merchandisers")]
    [RequirePermission(Permission.CreateMerchandiserAccounts)]
    public async Task<IActionResult> GetManagedMerchandiserAccounts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetManagedMerchandiserAccountsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Set one merchandiser's customers
    /// </summary>
    [HttpPut("merchandisers/{id:guid}/assigned-customers")]
    [RequirePermission(Permission.CreateMerchandiserAccounts)]
    public async Task<IActionResult> UpdateMerchandiserAssignedCustomers(
        Guid id,
        [FromBody] UpdateMerchandiserAssignedCustomersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateMerchandiserAssignedCustomersCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { message = "Merchandiser assignments updated successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Set the drivers' customers globally
    /// </summary>
    [HttpPut("drivers/assigned-customers")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UpdateGlobalDriverAssignedCustomers(
        [FromBody] UpdateGlobalDriverAssignedCustomersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateGlobalDriverAssignedCustomersCommand(request), cancellationToken);
        return result.Match(
            value => Ok(new { message = "Driver business partners updated successfully", updatedDriverCount = value }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Update user + permissions
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserDetailRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateUserCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { message = "User updated successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Delete user
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permission.DeleteUsers)]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteUserCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "User deleted successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// One user's permissions
    /// </summary>
    [HttpGet("{id:guid}/permissions")]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetUserPermissions(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetUserPermissionsQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Replace a user's permissions
    /// </summary>
    [HttpPut("{id:guid}/permissions")]
    [RequirePermission(Permission.ManageUserPermissions)]
    public async Task<IActionResult> UpdateUserPermissions(Guid id, [FromBody] UpdatePermissionsRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateUserPermissionsCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { message = "Permissions updated successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Every permission that can be granted
    /// </summary>
    [HttpGet("permissions/available")]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetAvailablePermissions(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAvailablePermissionsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Unlock a locked-out account
    /// </summary>
    [HttpPost("{id:guid}/unlock")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UnlockUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnlockUserCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "User account unlocked successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Clear a user's 2FA enrolment
    /// </summary>
    [HttpPost("{id:guid}/reset-2fa")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> ResetTwoFactor(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ResetTwoFactorCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "Two-factor authentication reset successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Get the signed-in user's own profile
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetCurrentUserQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The caller's own permissions
    /// </summary>
    [HttpGet("me/permissions")]
    public async Task<IActionResult> GetCurrentUserPermissions(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetCurrentUserPermissionsQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        return UserClaimReader.GetUserId(User);
    }
}
