using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.PostingDatePolicy.Commands.UpdatePostingDatePolicy;

/// <summary>Lets tills choose a sale's SAP posting date, or stops them.</summary>
public sealed record UpdatePostingDatePolicyCommand(bool AllowCustomPostingDate) : IRequest<ErrorOr<PostingDatePolicySettings>>;
