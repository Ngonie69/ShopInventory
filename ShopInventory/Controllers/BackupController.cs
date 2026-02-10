using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for Database Backup operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class BackupController : ControllerBase
{
    private readonly IBackupService _backupService;
    private readonly ILogger<BackupController> _logger;

    public BackupController(
        IBackupService backupService,
        ILogger<BackupController> logger)
    {
        _backupService = backupService;
        _logger = logger;
    }

    /// <summary>
    /// Get all backups
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewBackups)]
    [ProducesResponseType(typeof(BackupListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await _backupService.GetAllBackupsAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get backup by ID
    /// </summary>
    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewBackups)]
    [ProducesResponseType(typeof(BackupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var backup = await _backupService.GetBackupByIdAsync(id, cancellationToken);
        if (backup == null)
            return NotFound(new { message = $"Backup with ID {id} not found" });

        return Ok(backup);
    }

    /// <summary>
    /// Get backup statistics
    /// </summary>
    [HttpGet("stats")]
    [RequirePermission(Permission.ViewBackups)]
    [ProducesResponseType(typeof(BackupStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var result = await _backupService.GetAllBackupsAsync(cancellationToken);

        var totalSize = result.Backups.Sum(b => b.SizeBytes);
        var stats = new BackupStatsDto
        {
            TotalBackups = result.TotalCount,
            SuccessfulBackups = result.Backups.Count(b => b.Status == "Completed"),
            FailedBackups = result.Backups.Count(b => b.Status == "Failed"),
            TotalSizeBytes = totalSize,
            TotalSizeFormatted = FormatSize(totalSize),
            LastBackupAt = result.Backups.FirstOrDefault()?.StartedAt,
            NextScheduledBackup = null // Can be enhanced with scheduled backup feature
        };

        return Ok(stats);
    }

    /// <summary>
    /// Create a new backup
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.CreateBackups)]
    [ProducesResponseType(typeof(BackupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateBackupRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var backup = await _backupService.CreateBackupAsync(request, userId.Value, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = backup.Id }, backup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup");
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Restore a backup
    /// </summary>
    [HttpPost("{id}/restore")]
    [RequirePermission(Permission.RestoreBackups)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Restore(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var backup = await _backupService.GetBackupByIdAsync(id, cancellationToken);
        if (backup == null)
            return NotFound(new { message = $"Backup with ID {id} not found" });

        try
        {
            var result = await _backupService.RestoreBackupAsync(id, userId.Value, cancellationToken);
            if (result)
                return Ok(new { message = "Backup restored successfully" });

            return BadRequest(new { message = "Failed to restore backup" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring backup {Id}", id);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Download a backup file
    /// </summary>
    [HttpGet("{id}/download")]
    [RequirePermission(Permission.ViewBackups)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var backup = await _backupService.GetBackupByIdAsync(id, cancellationToken);
        if (backup == null)
            return NotFound(new { message = $"Backup with ID {id} not found" });

        var stream = await _backupService.DownloadBackupAsync(id, cancellationToken);
        if (stream == null)
            return NotFound(new { message = "Backup file not found" });

        return File(stream, "application/octet-stream", backup.FileName);
    }

    /// <summary>
    /// Delete a backup
    /// </summary>
    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeleteBackups)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var backup = await _backupService.GetBackupByIdAsync(id, cancellationToken);
        if (backup == null)
            return NotFound(new { message = $"Backup with ID {id} not found" });

        var result = await _backupService.DeleteBackupAsync(id, cancellationToken);
        if (!result)
            return BadRequest(new { message = "Failed to delete backup" });

        return NoContent();
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return null;

        return userId;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
