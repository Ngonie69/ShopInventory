using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Users.Queries.GetUsers;
using ShopInventory.Features.Users.Queries.GetUser;
using ShopInventory.Features.Users.Queries.GetRoles;
using ShopInventory.Features.Users.Commands.CreateUser;
using ShopInventory.Features.Users.Commands.UpdateUser;
using ShopInventory.Features.Users.Commands.DeleteUser;
using ShopInventory.Features.Users.Commands.AdminChangePassword;
using ShopInventory.Features.Users.Commands.UnlockUser;
using ShopInventory.Features.Users.Commands.DeactivateUser;
using ShopInventory.Features.Users.Commands.ActivateUser;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class UserController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetUsersQuery(page, pageSize, search, role), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetUserQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateUserCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetUser), new { id = value.User?.Id }, value),
            errors => Problem(errors));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateUserCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteUserCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("{id}/change-password")]
    public async Task<IActionResult> ChangePassword(Guid id, [FromBody] AdminChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AdminChangePasswordCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Password changed successfully" }), errors => Problem(errors));
    }

    [HttpPost("{id}/unlock")]
    public async Task<IActionResult> UnlockUser(Guid id, [FromBody] UnlockUserRequest? request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnlockUserCommand(id, request), cancellationToken);
        return result.Match(_ => Ok(new { Message = "User account unlocked successfully" }), errors => Problem(errors));
    }

    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> DeactivateUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeactivateUserCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { Message = "User account deactivated successfully" }), errors => Problem(errors));
    }

    [HttpPost("{id}/activate")]
    public async Task<IActionResult> ActivateUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ActivateUserCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { Message = "User account activated successfully" }), errors => Problem(errors));
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetRolesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
