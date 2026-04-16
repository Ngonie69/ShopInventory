using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.Backups.Queries.GetAllBackups;
using ShopInventory.Features.Backups.Queries.GetBackupById;
using ShopInventory.Features.Backups.Queries.GetBackupStats;
using ShopInventory.Features.Backups.Queries.DownloadBackup;
using ShopInventory.Features.Backups.Commands.CreateBackup;
using ShopInventory.Features.Backups.Commands.RestoreBackup;
using ShopInventory.Features.Backups.Commands.DeleteBackup;
using ShopInventory.Features.Backups.Commands.ResetDatabase;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class BackupController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewBackups)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAllBackupsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewBackups)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBackupByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("stats")]
    [RequirePermission(Permission.ViewBackups)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBackupStatsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    [RequirePermission(Permission.CreateBackups)]
    public async Task<IActionResult> Create([FromBody] CreateBackupRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new CreateBackupCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    [HttpPost("{id}/restore")]
    [RequirePermission(Permission.RestoreBackups)]
    public async Task<IActionResult> Restore(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new RestoreBackupCommand(id, userId.Value), cancellationToken);
        return result.Match(_ => Ok(new { message = "Backup restored successfully" }), errors => Problem(errors));
    }

    [HttpGet("{id}/download")]
    [RequirePermission(Permission.ViewBackups)]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DownloadBackupQuery(id), cancellationToken);
        return result.Match(
            value => File(value.FileStream, value.ContentType, value.FileName),
            errors => Problem(errors));
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeleteBackups)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteBackupCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("reset-database")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ResetDatabase(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var caller = userId?.ToString() ?? User.Identity?.Name ?? "Unknown";

        var result = await mediator.Send(new ResetDatabaseCommand(userId ?? Guid.Empty, caller), cancellationToken);
        return result.Match(
            _ => Ok(new { message = "Database has been reset successfully. All transactional data has been deleted." }),
            errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return null;

        return userId;
    }
}
