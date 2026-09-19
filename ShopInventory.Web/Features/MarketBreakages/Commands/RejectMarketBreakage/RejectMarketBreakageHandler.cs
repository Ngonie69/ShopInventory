using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.MarketBreakages.Commands.RejectMarketBreakage;

public sealed class RejectMarketBreakageHandler(
    IMarketBreakageService breakageService,
    IAuditService auditService,
    ILogger<RejectMarketBreakageHandler> logger)
    : IRequestHandler<RejectMarketBreakageCommand, ErrorOr<MarketBreakageDecisionResultDto>>
{
    public async Task<ErrorOr<MarketBreakageDecisionResultDto>> Handle(
        RejectMarketBreakageCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await breakageService.RejectAsync(request.Id, request.Remarks.Trim());
            await TryAuditAsync(request, success, message);
            return success && value is not null ? value : Errors.MarketBreakage.DecisionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error rejecting market breakage {Id}", request.Id);
            await TryAuditAsync(request, false, ex.Message);
            return Errors.MarketBreakage.DecisionFailed("The breakage could not be rejected.");
        }
    }

    private async Task TryAuditAsync(RejectMarketBreakageCommand request, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RejectMarketBreakage,
                "MarketBreakage",
                request.Id.ToString(),
                $"Rejected breakage report {request.Id}. {message} Reason: {request.Remarks}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit the rejection of market breakage {Id}", request.Id);
        }
    }
}
