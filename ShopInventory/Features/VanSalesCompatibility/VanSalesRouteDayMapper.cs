using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility;

/// <summary>
/// Turns a stored van day into the handset's view of it, and builds the envelope failures the rest of
/// this controller answers with.
/// </summary>
public static class VanSalesRouteDayMapper
{
    public static VanSalesRouteDayResponse Map(VanRouteDayEntity? day, string message) => new()
    {
        Status = 1,
        Message = message,
        Data = day is null ? null : MapDay(day)
    };

    public static VanSalesRouteDayResponse Failure(string message) => new()
    {
        Status = 0,
        Message = message,
        Data = null
    };

    public static VanSalesRouteDayDto MapDay(VanRouteDayEntity day) => new()
    {
        Id = day.Id,
        TradingDate = day.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        RouteCode = day.RouteCode,
        RouteName = day.RouteName,
        Territory = day.Territory,
        TruckRegNo = day.TruckRegNo,
        DepartedAt = FormatTimestamp(day.DepartedAt),
        StartingMileage = day.StartingMileage,
        PlannedCustomerCount = day.PlannedCustomerCount,
        ReturnedAt = day.ReturnedAt.HasValue ? FormatTimestamp(day.ReturnedAt.Value) : null,
        ClosingMileage = day.ClosingMileage,
        KilometresTravelled = day.KilometresTravelled,
        RtiOut = day.RtiOut,
        RtiReturned = day.RtiReturned,
        DeclaredCash = day.DeclaredCash,
        DeclaredEcocash = day.DeclaredEcocash,
        DeclaredInnbucks = day.DeclaredInnbucks,
        DeclaredCurrency = day.DeclaredCurrency,
        IsClosed = day.IsClosed,
        Notes = day.Notes
    };

    /// <summary>
    /// Timestamps go back in the clock they arrived in — CAT, no offset — matching
    /// <c>VanSalesAttendanceMapper</c>. The handset displays what it sent.
    /// </summary>
    private static string FormatTimestamp(DateTime utc) =>
        AuditService.ToCAT(utc).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
