using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;

/// <summary>Whether tills may choose a sale's SAP posting date, as last saved from Settings.</summary>
public sealed record GetPostingDatePolicyQuery : IRequest<ErrorOr<PostingDatePolicy>>;
