using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.PostingDatePolicy.Queries.GetPostingDatePolicy;

/// <summary>Whether tills may choose a sale's SAP posting date.</summary>
public sealed record GetPostingDatePolicyQuery : IRequest<ErrorOr<PostingDatePolicySettings>>;
