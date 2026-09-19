using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.MarketBreakages.Commands.ConfirmMarketBreakage;

public sealed class ConfirmMarketBreakageHandler(
    IMarketBreakageService breakageService,
    IAuditService auditService,
    ILogger<ConfirmMarketBreakageHandler> logger)
    : IRequestHandler<ConfirmMarketBreakageCommand, ErrorOr<MarketBreakageDecisionResultDto>>
{
    public async Task<ErrorOr<MarketBreakageDecisionResultDto>> Handle(
        ConfirmMarketBreakageCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await breakageService.ConfirmAsync(request.Id, new ConfirmMarketBreakageRequestDto
            {
                Lines = request.Lines.ToList(),
                Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim()
            });

            await TryAuditAsync(request, success, message, value?.Breakage.SapDocNum);

            return success && value is not null ? value : Errors.MarketBreakage.DecisionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error confirming market breakage {Id}", request.Id);
            await TryAuditAsync(request, false, ex.Message, null);
            return Errors.MarketBreakage.DecisionFailed("The breakage could not be confirmed.");
        }
    }

    private async Task TryAuditAsync(ConfirmMarketBreakageCommand request, bool success, string message, int? docNum)
    {
        try
        {
            var counted = request.Lines.Sum(line => line.ConfirmedQuantity);
            await auditService.LogAsync(
                AuditActions.ConfirmMarketBreakage,
                "MarketBreakage",
                request.Id.ToString(),
                $"Confirmed breakage report {request.Id}: {counted:0.##} unit(s) counted"
                    + (docNum is int number ? $", transfer #{number} to returns." : ".")
                    + $" {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit the confirmation of market breakage {Id}", request.Id);
        }
    }
}
