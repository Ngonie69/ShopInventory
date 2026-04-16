using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.UserManagement.Queries.GetUsers;
using ShopInventory.Features.UserManagement.Queries.GetUser;
using ShopInventory.Features.UserManagement.Queries.GetUserPermissions;
using ShopInventory.Features.UserManagement.Queries.GetAvailablePermissions;
using ShopInventory.Features.UserManagement.Queries.GetCurrentUser;
using ShopInventory.Features.UserManagement.Queries.GetCurrentUserPermissions;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUser;
using ShopInventory.Features.UserManagement.Commands.DeleteUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUserPermissions;
using ShopInventory.Features.UserManagement.Commands.UnlockUser;
using ShopInventory.Features.UserManagement.Commands.ResetTwoFactor;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class UserManagementController(IMediator mediator) : ApiControllerBase
{
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

    [HttpGet("{id:guid}")]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetUserQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    [RequirePermission(Permission.CreateUsers)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDetailRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateUserCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetUser), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserDetailRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateUserCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { message = "User updated successfully" }), errors => Problem(errors));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permission.DeleteUsers)]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteUserCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "User deleted successfully" }), errors => Problem(errors));
    }

    [HttpGet("{id:guid}/permissions")]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetUserPermissions(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetUserPermissionsQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPut("{id:guid}/permissions")]
    [RequirePermission(Permission.ManageUserPermissions)]
    public async Task<IActionResult> UpdateUserPermissions(Guid id, [FromBody] UpdatePermissionsRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateUserPermissionsCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { message = "Permissions updated successfully" }), errors => Problem(errors));
    }

    [HttpGet("permissions/available")]
    [RequirePermission(Permission.ViewUsers)]
    public async Task<IActionResult> GetAvailablePermissions(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAvailablePermissionsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("{id:guid}/unlock")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UnlockUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnlockUserCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "User account unlocked successfully" }), errors => Problem(errors));
    }

    [HttpPost("{id:guid}/reset-2fa")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> ResetTwoFactor(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ResetTwoFactorCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "Two-factor authentication reset successfully" }), errors => Problem(errors));
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetCurrentUserQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

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
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return null;

        return userId;
    }
}
