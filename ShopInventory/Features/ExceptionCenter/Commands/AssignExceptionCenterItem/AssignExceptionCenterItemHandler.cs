using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;

public sealed class AssignExceptionCenterItemHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AssignExceptionCenterItemHandler> logger
) : IRequestHandler<AssignExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AssignExceptionCenterItemCommand command,
        CancellationToken cancellationToken)
    {
        var source = ExceptionCenterSources.Normalize(command.Source);
        if (!await ExceptionCenterItemLookup.ExistsAsync(context, source, command.ItemKey, cancellationToken))
        {
            return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
        }

        var (userId, username) = ResolveCurrentUser();
        if (string.IsNullOrWhiteSpace(username) || string.Equals(username, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.ExceptionCenter.UpdateFailed("Assign", "Could not resolve the current user for assignment.");
        }

        var state = await ExceptionCenterItemLookup.GetOrCreateStateAsync(
            context, source, command.ItemKey, cancellationToken);

        state.AssignedToUserId = userId;
        state.AssignedToUsername = username;
        state.AssignedAtUtc = DateTime.UtcNow;
        state.UpdatedAtUtc = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Assigned exception center item {Source}:{ItemKey} to {Username}", source, command.ItemKey, username);
        return Result.Success;
    }

    private (Guid? userId, string username) ResolveCurrentUser()
    {
        var user = httpContextAccessor.HttpContext?.User;
        var username = user?.Identity?.Name
            ?? user?.FindFirst(ClaimTypes.Name)?.Value
            ?? "Unknown";

        var userIdClaim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId)
            ? (userId, username)
            : (null, username);
    }
}