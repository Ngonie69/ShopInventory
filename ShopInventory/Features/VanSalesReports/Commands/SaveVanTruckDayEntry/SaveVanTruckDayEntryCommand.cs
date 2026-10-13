using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Commands.SaveVanTruckDayEntry;

/// <summary>
/// Records what the office knows about one truck's day: fuel issued, account sales and the area worked.
/// </summary>
/// <remarks>
/// One entry per route per trading day, written over in place. Every field is optional and is taken as
/// given, so a blank clears what was there; saving all of them blank removes the entry. See
/// <see cref="Models.Entities.VanTruckDayEntryEntity"/> for why each of the three is the office's to say.
/// </remarks>
public sealed record SaveVanTruckDayEntryCommand(
    int RouteId,
    DateTime TradingDate,
    string? AreaName,
    decimal? FuelLitres,
    decimal? AccountSales,
    string? Notes,
    Guid? ActingUserId
) : IRequest<ErrorOr<VanTruckDayEntryDto>>;

/// <summary>
/// The office's entry for a truck-day as it now stands. <c>HasEntry</c> is false when the save left
/// nothing behind, in which case every other field is null.
/// </summary>
public sealed record VanTruckDayEntryDto(
    int RouteId,
    DateTime TradingDate,
    bool HasEntry,
    string? AreaName,
    decimal? FuelLitres,
    decimal? AccountSales,
    string? Notes,
    string? UpdatedByName,
    DateTime? UpdatedAtUtc);
