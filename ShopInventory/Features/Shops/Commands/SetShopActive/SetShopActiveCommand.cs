using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Commands.SetShopActive;

/// <summary>
/// Opens or closes a shop. <c>UserId</c> is the administrator doing it, recorded on the row.
/// </summary>
/// <remarks>
/// One command for both directions rather than a Deactivate and a separate Reopen, and deliberately
/// not a field on the edit form. Closing has a rule attached — operators must be reassigned first —
/// and a checkbox on an update request would let a save walk past it. Keeping the transition here
/// gives the column one writer.
/// </remarks>
public sealed record SetShopActiveCommand(
    int ShopId,
    bool IsActive,
    Guid UserId
) : IRequest<ErrorOr<ShopDto>>;
