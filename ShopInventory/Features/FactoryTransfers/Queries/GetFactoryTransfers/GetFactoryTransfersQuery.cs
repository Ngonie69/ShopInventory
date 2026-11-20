using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfers;

/// <param name="UserId">The signed-in user asking; decides which transfers are theirs to decide.</param>
/// <param name="View">
/// <c>awaiting</c> (in transit and mine to decide), <c>open</c> (not yet settled), <c>all</c>, or one
/// status by name.
/// </param>
/// <param name="Search">Optional text matched anywhere in the sender, either warehouse, the vehicle plate, the driver or an item code, or a transfer number exactly. At most 100 characters.</param>
/// <param name="Page">The page to return, counting from 1.</param>
/// <param name="PageSize">How many transfers a page holds, 1 to 100.</param>
public sealed record GetFactoryTransfersQuery(
    Guid UserId,
    string? View,
    string? Search,
    int Page,
    int PageSize) : IRequest<ErrorOr<FactoryTransferListResponseDto>>
{
    public const string Awaiting = "awaiting";
    public const string Open = "open";
    public const string All = "all";
}
