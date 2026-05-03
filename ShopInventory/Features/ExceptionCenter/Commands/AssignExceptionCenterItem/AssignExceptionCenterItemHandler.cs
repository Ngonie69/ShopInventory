using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

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
        var source = NormalizeSource(command.Source);
        if (!await ItemExistsAsync(source, command.ItemId, cancellationToken))
        {
            return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemId);
        }

        var (userId, username) = ResolveCurrentUser();
        if (string.IsNullOrWhiteSpace(username) || string.Equals(username, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.ExceptionCenter.UpdateFailed("Assign", "Could not resolve the current user for assignment.");
        }

        var state = await context.ExceptionCenterItemStates
            .FirstOrDefaultAsync(s => s.Source == source && s.ItemId == command.ItemId, cancellationToken);

        if (state == null)
        {
            state = new ExceptionCenterItemStateEntity
            {
                Source = source,
                ItemId = command.ItemId
            };

            context.ExceptionCenterItemStates.Add(state);
        }

        state.AssignedToUserId = userId;
        state.AssignedToUsername = username;
        state.AssignedAtUtc = DateTime.UtcNow;
        state.UpdatedAtUtc = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Assigned exception center item {Source}:{ItemId} to {Username}", source, command.ItemId, username);
        return Result.Success;
    }

    private async Task<bool> ItemExistsAsync(string source, int itemId, CancellationToken cancellationToken)
        => source switch
        {
            "invoice-queue" => await context.InvoiceQueue.AsNoTracking().AnyAsync(q => q.Id == itemId, cancellationToken),
            "inventory-transfer-queue" => await context.InventoryTransferQueue.AsNoTracking().AnyAsync(q => q.Id == itemId, cancellationToken),
            "mobile-order-post-processing" => await context.MobileOrderPostProcessingQueue.AsNoTracking().AnyAsync(q => q.Id == itemId, cancellationToken),
            "payment-callback" => await context.PaymentTransactions.AsNoTracking().AnyAsync(q => q.Id == itemId, cancellationToken),
            "payment-callback-rejection" or "credit-note-fiscalization" => await context.ExceptionCenterIncidents.AsNoTracking().AnyAsync(q => q.Id == itemId && q.Source == source, cancellationToken),
            _ => false
        };

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

    private static string NormalizeSource(string source) => source.Trim().ToLowerInvariant();
}