using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetTelematicsVehicles;

/// <summary>
/// The vehicles the telematics provider knows about, for assigning one to a route.
/// </summary>
/// <remarks>
/// <c>IncludeRetired</c> brings back vehicles that have left the fleet, which a route that still
/// names one needs so the picker can show what it is set to rather than silently clearing it.
/// </remarks>
public sealed record GetTelematicsVehiclesQuery(bool IncludeRetired = false)
    : IRequest<ErrorOr<TelematicsVehiclesResult>>;

/// <summary>
/// The fleet, plus enough about the integration for the page to explain an empty list.
/// </summary>
/// <remarks>
/// <c>Reason</c> is a sentence the page prints verbatim. A result that said only "empty" would
/// force the page to guess between "telematics is switched off", "no credentials", "the sync has
/// not run yet" and "this account genuinely has no vehicles" — four different things to do next.
/// </remarks>
public sealed record TelematicsVehiclesResult(
    bool Enabled,
    bool Configured,
    DateTime? LastSyncedAt,
    string? Reason,
    List<TelematicsVehicleDto> Vehicles);

/// <remarks>
/// <c>Registration</c> is the plate as the provider spells it; <c>RegistrationNormalized</c> is
/// letters and digits only, upper case, and is what the report joins on.
/// <c>ClientVehicleName</c> is the fleet's own name for the vehicle, which on this account is a
/// yard number joined to the plate ("306_AFQ9644") — shown as a hint, never matched on.
/// <c>StateLabel</c> says why a vehicle may report nothing (in the workshop, tracker being
/// repaired, retired from the fleet) and is null when there is nothing to say.
/// </remarks>
public sealed record TelematicsVehicleDto(
    string Registration,
    string RegistrationNormalized,
    string? ClientVehicleName,
    string? Description,
    bool HasAnyFuelSensor,
    bool? HasTemperatureProbe,
    bool IsActiveInFleet,
    string? StateLabel);
