using ErrorOr;
using MediatR;
using ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdatePostingDatePolicy;

/// <summary>Lets tills choose a sale's SAP posting date, or stops them.</summary>
public sealed record UpdatePostingDatePolicyCommand(
    Guid CallerUserId,
    string? CallerName,
    bool AllowCustomPostingDate
) : IRequest<ErrorOr<PostingDatePolicy>>;
