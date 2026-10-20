using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// Claims an in-transit transfer for a decision — accepted, rejected or recalled — together with the
/// quantity each line takes, in one transaction.
/// </summary>
/// <remarks>
/// The claim is a conditional update on <see cref="FactoryTransferStatuses.InTransit"/>, so two people
/// deciding at once cannot both win. The line quantities commit with it: a decision recorded without
/// them would post an acceptance that sends every unit back.
/// </remarks>
public static class FactoryTransferSettlement
{
    /// <returns>False when the transfer had already left <see cref="FactoryTransferStatuses.InTransit"/>.</returns>
    public static async Task<bool> ClaimAsync(
        ApplicationDbContext context,
        int transferId,
        string decision,
        FactoryTransferActor actor,
        string? reason,
        IReadOnlyDictionary<int, decimal> acceptedByLineId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var userId = actor.UserId;
        var name = actor.Name;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await context.FactoryTransfers
            .Where(transfer => transfer.Id == transferId && transfer.Status == FactoryTransferStatuses.InTransit)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(transfer => transfer.Status, FactoryTransferStatuses.Settling)
                .SetProperty(transfer => transfer.Decision, decision)
                .SetProperty(transfer => transfer.DecidedByUserId, userId)
                .SetProperty(transfer => transfer.DecidedByName, name)
                .SetProperty(transfer => transfer.DecidedAtUtc, now)
                .SetProperty(transfer => transfer.DecisionReason, reason),
                cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var lineIds = await context.FactoryTransferLines
            .Where(line => line.TransferId == transferId)
            .Select(line => line.Id)
            .ToListAsync(cancellationToken);

        foreach (var lineId in lineIds)
        {
            var accepted = acceptedByLineId.TryGetValue(lineId, out var quantity) ? quantity : 0m;
            await context.FactoryTransferLines
                .Where(line => line.Id == lineId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(line => line.AcceptedQuantity, accepted), cancellationToken);
        }

        // Committed on a token nothing cancels: past here the decision is recorded and must be posted.
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }
}
