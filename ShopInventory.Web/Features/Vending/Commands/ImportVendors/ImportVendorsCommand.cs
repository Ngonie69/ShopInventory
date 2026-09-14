using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending.Commands.ImportVendors;

/// <summary>Checks (<paramref name="ValidateOnly"/>) or adds the vendors read off an upload.</summary>
public sealed record ImportVendorsCommand(
    IReadOnlyList<ImportVendorRowModel> Rows,
    bool ValidateOnly
) : IRequest<ErrorOr<ImportVendorsResultModel>>;
