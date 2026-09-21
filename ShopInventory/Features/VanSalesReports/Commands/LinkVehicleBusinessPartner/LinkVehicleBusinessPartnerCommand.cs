using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesReports.Queries.GetTelematicsVehicles;

namespace ShopInventory.Features.VanSalesReports.Commands.LinkVehicleBusinessPartner;

/// <summary>
/// Points a vehicle at the van sales business partner whose takings and stock it carries, or
/// clears the link when <c>BusinessPartnerCode</c> is null or blank.
/// </summary>
/// <remarks>
/// The name travels with the code because the API has no business partner master of its own —
/// the cache lives in the portal — and the fleet page has to read without a second lookup. It is
/// a label, not an identifier: everything joins on the code.
/// </remarks>
public sealed record LinkVehicleBusinessPartnerCommand(
    string Registration,
    string? BusinessPartnerCode,
    string? BusinessPartnerName
) : IRequest<ErrorOr<TelematicsVehicleDto>>;
