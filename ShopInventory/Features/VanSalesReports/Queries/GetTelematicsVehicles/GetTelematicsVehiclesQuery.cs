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

/// <param name="Registration">The plate as the provider spells it.</param>
/// <param name="RegistrationNormalized">
/// Letters and digits only, upper case — what the report joins on.
/// </param>
/// <param name="ClientVehicleName">
/// The fleet's own name for it, which on this account is a number joined to the plate
/// ("306_AFQ9644"). Shown as a hint beside the registration, never matched on.
/// </param>
/// <param name="StateLabel">
/// Why a vehicle may report nothing — in the workshop, tracker being repaired, or retired from
/// the fleet. Null when there is nothing to say.
/// </param>
public sealed record TelematicsVehicleDto(
    string Registration,
    string RegistrationNormalized,
    string? ClientVehicleName,
    string? Description,
    bool HasAnyFuelSensor,
    bool? HasTemperatureProbe,
    bool IsActiveInFleet,
    string? StateLabel);
