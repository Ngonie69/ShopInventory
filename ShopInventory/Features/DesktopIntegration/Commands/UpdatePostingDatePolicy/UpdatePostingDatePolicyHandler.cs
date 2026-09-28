using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy.PostingDatePolicyKeys;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdatePostingDatePolicy;

public sealed class UpdatePostingDatePolicyHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    IHubContext<NotificationHub> hub,
    ILogger<UpdatePostingDatePolicyHandler> logger)
    : IRequestHandler<UpdatePostingDatePolicyCommand, ErrorOr<PostingDatePolicy>>
{
    /// <summary>
    /// What an open till hears when the switch changes. The payload is the same policy
    /// <c>GET sales/posting-date-policy</c> answers, so the till reads both with one model.
    /// </summary>
    public const string ChangedMethodName = "PostingDatePolicyChanged";

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

        var policy = await mediator.Send(new GetPostingDatePolicyQuery(), cancellationToken);
        if (!policy.IsError)
        {
            await TellTheTillsAsync(policy.Value);
        }

        return policy;
    }

    /// <summary>
    /// Pushes the new policy to every connected till, so a sale screen already open unlocks or locks its
    /// date picker at once instead of on its next sale.
    /// </summary>
    /// <remarks>
    /// Sent to <c>all</c> because the switch is platform-wide, not per shop. Never fails the save: the
    /// platform refuses a date other than today while the switch is off whatever a till believes, and a
    /// till that misses the push reads the policy again when a sale screen opens.
    /// </remarks>
    private async Task TellTheTillsAsync(PostingDatePolicy policy)
    {
        try
        {
            await hub.Clients.Group("all").SendAsync(ChangedMethodName, policy, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not push the posting date policy change to the tills");
        }
    }
}
