using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Vending.Commands.ImportVendors;

/// <summary>Checks, or adds, a sheet of vendors across the vending depots.</summary>
/// <param name="Request">The rows as read off the sheet, and whether only to check them.</param>
/// <param name="UserId">The signed-in operator, recorded as each new vendor's creator.</param>
public sealed record ImportVendorsCommand(
    ImportVendorsRequest Request,
    Guid UserId) : IRequest<ErrorOr<ImportVendorsResultDto>>;
