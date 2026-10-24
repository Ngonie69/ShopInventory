namespace ShopInventory.Web.Models;

/// <summary>
/// One Departure Compliance row as the page shows it, for the Excel export: the day, plus the three
/// texts the page works out for it and the workbook should not work out a second time.
/// </summary>
public sealed record DepartureComplianceExportRow(
    DepartureComplianceDay Day,
    string Flags,
    string Signals,
    string TelematicsState);

/// <summary>
/// One call on the Van Activity page, for the Excel export. The times are already on the CAT clock and
/// the fix already named, as the page shows them.
/// </summary>
public sealed record VanActivityExportRow(
    string RepName,
    DateTime TradingDay,
    VanVisitDto Visit,
    DateTime CheckInCat,
    DateTime? CheckOutCat,
    string Fix);
