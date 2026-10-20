using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfers;

/// <param name="View">
/// <c>awaiting</c> (in transit and mine to decide), <c>open</c> (not yet settled), <c>all</c>, or one
/// status by name.
/// </param>
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
