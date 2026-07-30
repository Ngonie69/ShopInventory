using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;

public sealed class AcknowledgeExceptionCenterItemHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AcknowledgeExceptionCenterItemHandler> logger
) : IRequestHandler<AcknowledgeExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AcknowledgeExceptionCenterItemCommand command,
        CancellationToken cancellationToken)
    {
        var source = ExceptionCenterSources.Normalize(command.Source);
        if (!await ExceptionCenterItemLookup.ExistsAsync(context, source, command.ItemKey, cancellationToken))
        {
            return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
        }

        var state = await ExceptionCenterItemLookup.GetOrCreateStateAsync(
            context, source, command.ItemKey, cancellationToken);

        var (userId, username) = ResolveCurrentUser();
        state.IsAcknowledged = true;
        state.AcknowledgedAtUtc = DateTime.UtcNow;
        state.AcknowledgedByUserId = userId;
        state.AcknowledgedByUsername = username;
        state.UpdatedAtUtc = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Acknowledged exception center item {Source}:{ItemKey} by {Username}", source, command.ItemKey, username);
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