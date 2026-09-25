using ErrorOr;
using MediatR;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy.PostingDatePolicyKeys;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdatePostingDatePolicy;

public sealed class UpdatePostingDatePolicyHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    ILogger<UpdatePostingDatePolicyHandler> logger)
    : IRequestHandler<UpdatePostingDatePolicyCommand, ErrorOr<PostingDatePolicy>>
{
    public async Task<ErrorOr<PostingDatePolicy>> Handle(
        UpdatePostingDatePolicyCommand request, CancellationToken cancellationToken)
    {
        var who = string.IsNullOrWhiteSpace(request.CallerName) ? request.CallerUserId.ToString() : request.CallerName;

        await StageAsync(db, AllowCustomPostingDate, "bool", request.AllowCustomPostingDate ? "true" : "false",
            "Whether a till may choose the date its sale is posted to SAP under (posting, due and document date). Off posts on the day of sale.",
            cancellationToken);
        await StageAsync(db, ChangedBy, "string", who,
            "The admin who last switched custom posting dates on or off.", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Custom posting dates for till sales switched {State} by {User}",
            request.AllowCustomPostingDate ? "on" : "off", who);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdatePostingDatePolicy,
                "DesktopSales",
                null,
                $"Custom SAP posting dates for till sales switched {(request.AllowCustomPostingDate ? "on" : "off")} by {who}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the posting date policy change");
        }

        return await mediator.Send(new GetPostingDatePolicyQuery(), cancellationToken);
    }
}
