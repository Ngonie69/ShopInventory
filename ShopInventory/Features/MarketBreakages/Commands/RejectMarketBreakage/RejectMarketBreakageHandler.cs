using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.MarketBreakages.Commands.RejectMarketBreakage;

public sealed class RejectMarketBreakageHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<RejectMarketBreakageHandler> logger)
    : IRequestHandler<RejectMarketBreakageCommand, ErrorOr<MarketBreakageDecisionResultDto>>
{
    public async Task<ErrorOr<MarketBreakageDecisionResultDto>> Handle(
        RejectMarketBreakageCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var remarks = command.Remarks!.Trim();
        var userId = command.UserId;
        var userName = await MarketBreakageActor.ResolveNameAsync(context, userId, cancellationToken);
        if (userName is null)
            return Errors.MarketBreakage.UserNotFound;

        // Conditional, so a reject cannot land on a report somebody is transferring at that moment:
        // a confirm claims the report as Transferring before it posts, and that is not a state a
        // reject may start from. See MarketBreakageStatuses.MayReject.
        var updated = await context.MarketBreakages
            .Where(breakage => breakage.Id == command.BreakageId
                && (breakage.Status == MarketBreakageStatuses.Pending
                    || breakage.Status == MarketBreakageStatuses.TransferFailed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(breakage => breakage.Status, MarketBreakageStatuses.Rejected)
                .SetProperty(breakage => breakage.DecidedByUserId, userId)
                .SetProperty(breakage => breakage.DecidedByName, userName)
                .SetProperty(breakage => breakage.DecidedAtUtc, now)
                .SetProperty(breakage => breakage.DecisionRemarks, remarks),
                cancellationToken);

        var detail = await context.MarketBreakages
            .AsNoTracking()
            .Where(breakage => breakage.Id == command.BreakageId)
            .Select(MarketBreakageProjections.Detail)
            .FirstOrDefaultAsync(cancellationToken);

        if (detail is null)
            return Errors.MarketBreakage.NotFound(command.BreakageId);

        if (updated == 0)
            return Errors.MarketBreakage.NotActionable(detail.Status);

        logger.LogInformation("Market breakage {BreakageId} rejected by {UserId}", command.BreakageId, command.UserId);

        try
        {
            await auditService.LogAsync(
                AuditActions.RejectMarketBreakage, "MarketBreakage", command.BreakageId.ToString(),
                $"Breakage report #{command.BreakageId} from {detail.ReportedByName} ({detail.VanWarehouseCode}) rejected: {command.Remarks}",
                true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the rejection of market breakage {BreakageId}", command.BreakageId);
        }

        return new MarketBreakageDecisionResultDto
        {
            Message = $"Breakage report #{command.BreakageId} rejected. Nothing was transferred.",
            Breakage = detail
        };
    }
}
