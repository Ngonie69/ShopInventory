using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Commands.CreateStockWriteOff;

/// <summary>
/// Writes counted stock off a warehouse, as a SAP goods issue.
/// </summary>
/// <param name="Request">What is leaving, out of where, and why.</param>
/// <param name="UserId">
/// The account the write-off is recorded against. Taken from the token rather than the body: a
/// write-off destroys inventory value, and who did it is not the caller's to name.
/// </param>
public sealed record CreateStockWriteOffCommand(
    CreateStockWriteOffRequestDto Request,
    Guid UserId) : IRequest<ErrorOr<StockWriteOffResultDto>>;
