using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;

/// <remarks>
/// <c>Search</c> is matched against item code and item name; null returns everything. Clearing
/// <c>IncludeInactive</c> keeps retired factors out of the list.
/// </remarks>
public sealed record GetItemVolumeConversionsQuery(
    string? Search = null,
    bool IncludeInactive = true
) : IRequest<ErrorOr<GetItemVolumeConversionsResult>>;
