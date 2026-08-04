using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;

public sealed record GetItemVolumeConversionsQuery(
    string? Search = null,
    bool IncludeInactive = true
) : IRequest<ErrorOr<GetItemVolumeConversionsResult>>;
