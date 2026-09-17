using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesPodDeliveries;

/// <summary>
/// The invoices on the drivers' shop list, for a van rep to file delivery notes against.
/// </summary>
/// <remarks>
/// The portal's POD report already answers this for a <c>Driver</c> — <c>invoice/pod-upload-status</c>
/// scopes itself to the shops on the list — but that route is gated on a role list a van rep is not on,
/// and a van rep is <c>Sales</c> or <c>ADR</c>, so even let through it would scope them to nothing and
/// hand back every invoice in the company. This asks the same report with the scope stated explicitly.
/// </remarks>
/// <param name="UserId">The signed-in account.</param>
/// <param name="FromDate">First invoice date, as a calendar day.</param>
/// <param name="ToDate">Last invoice date, inclusive.</param>
public sealed record GetVanSalesPodDeliveriesQuery(
    Guid UserId,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<VanSalesPodDeliveriesDto>>;
