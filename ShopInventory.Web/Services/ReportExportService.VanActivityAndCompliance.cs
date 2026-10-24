using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The Departure Compliance and Van Activity workbooks, which replaced the two pages' CSV exports.
/// </summary>
/// <remarks>
/// <para>
/// One table per sheet, in the column order the CSVs had, so a pivot rebuilt on the workbook finds
/// its columns where they were. Unlike the CSV, every figure is a number, every day a date and every
/// rate a percentage, so they can be summed and filtered without a text-to-columns pass.
/// </para>
/// <para>
/// Text is written as a value, never as a formula, so notes typed by reps and names from the handset
/// cannot run on open: the apostrophe guard the CSVs needed has nothing to guard here.
/// </para>
/// </remarks>
public partial class ReportExportService
{
    private const string ComplianceMoneyFormat = "#,##0.00";
    private const string ComplianceRateFormat = "0.0%";
    private const string ComplianceDayFormat = "yyyy-mm-dd";

    private static readonly string[] DepartureComplianceHeaders =
    [
        "Trading date", "Rep", "Username", "Territory", "Route code", "Route", "Truck", "Time out", "Time in", "Day closed",
        "Customers planned", "Customers visited", "Productive calls", "CCR", "PCR", "AOV",
        "Sales", "Currency", "Cash", "Ecocash", "Innbucks", "Swipe", "Untendered", "Declarable takings",
        "Declared cash", "Declared ecocash", "Declared innbucks", "Declared total", "Variance",
        "Shortfall", "Overage",
        "Odometer out", "Odometer in", "Kilometres", "New customers", "Returns out", "Returns back", "Flags", "Notes",
        "Verified out", "Departure source", "Departure delta (min)", "Vehicle ignition on",
        "Vehicle ignition off", "Vehicle left depot", "Ignition cycles", "Driving (min)", "Idle (min)",
        "Vehicle distance (km)", "Odometer divergence (km)", "Odometer reset", "Tracker changed",
        "Telematics state", "Signals",
        "Other currency takings", "Other currencies"
    ];

    private static readonly string[] VanActivityHeaders =
    [
        "Rep", "Van", "Route", "Trading day", "Customer code", "Customer", "In CAT", "Out CAT", "Duration minutes",
        "Latitude", "Longitude", "Fix", "Accuracy m", "Captured offline", "Check-in notes", "Check-out notes"
    ];

    public byte[] ExportDepartureComplianceToExcel(
        IReadOnlyList<DepartureComplianceExportRow> rows,
        DateTime? fromDate,
        DateTime? toDate)
    {
        using var workbook = NewWorkbook("Departure Compliance");
        var now = DateTime.UtcNow.AddHours(2); // CAT
        var lastCol = DepartureComplianceHeaders.Length;

        var ws = workbook.Worksheets.Add("Rep-days");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, $"DEPARTURE COMPLIANCE  —  {PeriodText(fromDate, toDate)}", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol, DepartureComplianceHeaders);

        var index = 0;
        foreach (var exported in rows)
        {
            var day = exported.Day;
            var c = 1;
            TsDataRow(ws, row, lastCol, index % 2 == 1);

            PutDay(ws.Cell(row, c++), day.TradingDate);
            PutText(ws.Cell(row, c++), day.DisplayName);
            PutText(ws.Cell(row, c++), day.Username);
            PutText(ws.Cell(row, c++), day.Territory);
            PutText(ws.Cell(row, c++), day.RouteCode);
            PutText(ws.Cell(row, c++), day.RouteName);
            PutText(ws.Cell(row, c++), day.TruckRegNo);
            PutText(ws.Cell(row, c++), Clock(day.TimeOut));
            PutText(ws.Cell(row, c++), Clock(day.TimeIn));
            PutText(ws.Cell(row, c++), day.HasDayRecord ? (day.IsClosed ? "yes" : "no") : "no day record");
            PutNumber(ws.Cell(row, c++), day.PlannedCustomerCount);
            PutNumber(ws.Cell(row, c++), day.CustomersVisited);
            PutNumber(ws.Cell(row, c++), day.ProductiveCalls);
            PutRate(ws.Cell(row, c++), day.CallComplianceRate);
            PutRate(ws.Cell(row, c++), day.ProductiveCallRate);
            PutMoney(ws.Cell(row, c++), day.AverageOrderValue);
            PutMoney(ws.Cell(row, c++), day.SystemTotalSales);
            PutText(ws.Cell(row, c++), day.Currency);
            PutMoney(ws.Cell(row, c++), day.SystemCash);
            PutMoney(ws.Cell(row, c++), day.SystemEcocash);
            PutMoney(ws.Cell(row, c++), day.SystemInnbucks);
            PutMoney(ws.Cell(row, c++), day.SystemOther);
            PutMoney(ws.Cell(row, c++), day.SystemUntendered);
            PutMoney(ws.Cell(row, c++), day.SystemDeclarableTakings);
            PutMoney(ws.Cell(row, c++), day.DeclaredCash);
            PutMoney(ws.Cell(row, c++), day.DeclaredEcocash);
            PutMoney(ws.Cell(row, c++), day.DeclaredInnbucks);
            PutMoney(ws.Cell(row, c++), day.DeclaredTotal);
            PutMoney(ws.Cell(row, c++), day.DeclaredVariance);
            // Blank where there is no finding, which is not a finding of zero.
            PutMoney(ws.Cell(row, c++), day.DeclaredShortfall);
            PutMoney(ws.Cell(row, c++), day.DeclaredOverage);
            PutNumber(ws.Cell(row, c++), day.StartingMileage);
            PutNumber(ws.Cell(row, c++), day.ClosingMileage);
            PutNumber(ws.Cell(row, c++), day.KilometresTravelled);
            PutNumber(ws.Cell(row, c++), day.NewCustomers);
            PutNumber(ws.Cell(row, c++), day.RtiOut);
            PutNumber(ws.Cell(row, c++), day.RtiReturned);
            PutText(ws.Cell(row, c++), exported.Flags);
            PutText(ws.Cell(row, c++), day.Notes);
            PutText(ws.Cell(row, c++), Clock(day.EffectiveDeparture));
            // Which clock decided the day: a sheet of times says nothing about where each came from
            // unless this column is beside them.
            PutText(ws.Cell(row, c++), day.DepartureIsVerified ? "vehicle" : day.HasDayRecord ? "handset" : null);
            PutNumber(ws.Cell(row, c++), day.DepartureDiscrepancyMinutes);
            PutText(ws.Cell(row, c++), Clock(day.Telematics?.FirstIgnitionOn));
            PutText(ws.Cell(row, c++), Clock(day.Telematics?.LastIgnitionOff));
            PutText(ws.Cell(row, c++), Clock(day.Telematics?.FirstDeparture));
            PutNumber(ws.Cell(row, c++), day.Telematics?.IgnitionCycleCount);
            PutNumber(ws.Cell(row, c++), day.Telematics?.DrivingMinutes);
            PutNumber(ws.Cell(row, c++), day.Telematics?.IdleMinutes);
            PutNumber(ws.Cell(row, c++), day.Telematics?.DistanceKm);
            PutNumber(ws.Cell(row, c++), day.OdometerDivergenceKm);
            PutText(ws.Cell(row, c++), day.Telematics?.OdometerReset == true ? "yes" : null);
            PutText(ws.Cell(row, c++), day.Telematics?.TerminalChanged == true ? "yes" : null);
            PutText(ws.Cell(row, c++), exported.TelematicsState);
            // Its own column rather than folded into Flags, so Flags keeps meaning compliance findings.
            PutText(ws.Cell(row, c++), exported.Signals);
            PutMoney(ws.Cell(row, c++), day.OtherCurrencySales);
            PutText(ws.Cell(row, c), day.OtherCurrencyCodes);

            row++;
            index++;
        }

        FinishVanTable(ws, headerRow, row - 1, lastCol, rows.Count == 0
            ? "No rep-days match the filters this was exported with."
            : null);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportVanActivityToExcel(
        IReadOnlyList<VanActivityExportRow> rows,
        DateTime? fromDate,
        DateTime? toDate)
    {
        using var workbook = NewWorkbook("Van Activity");
        var now = DateTime.UtcNow.AddHours(2); // CAT
        var lastCol = VanActivityHeaders.Length;

        var ws = workbook.Worksheets.Add("Calls");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, $"VAN ACTIVITY  —  {PeriodText(fromDate, toDate)}", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol, VanActivityHeaders);

        var index = 0;
        foreach (var call in rows)
        {
            var visit = call.Visit;
            var c = 1;
            TsDataRow(ws, row, lastCol, index % 2 == 1);

            PutText(ws.Cell(row, c++), call.RepName);
            PutText(ws.Cell(row, c++), visit.TruckRegNo);
            PutText(ws.Cell(row, c++), visit.RouteName);
            PutDay(ws.Cell(row, c++), call.TradingDay);
            PutText(ws.Cell(row, c++), visit.CustomerCode);
            PutText(ws.Cell(row, c++), visit.CustomerName);
            PutText(ws.Cell(row, c++), Clock(call.CheckInCat));
            PutText(ws.Cell(row, c++), call.CheckOutCat is { } outCat ? Clock(outCat) : "Open");
            PutWhole(ws.Cell(row, c++), visit.DurationMinutes);
            PutCoordinate(ws.Cell(row, c++), visit.CheckInLatitude);
            PutCoordinate(ws.Cell(row, c++), visit.CheckInLongitude);
            PutText(ws.Cell(row, c++), call.Fix);
            PutWhole(ws.Cell(row, c++), visit.CheckInLocationAccuracyMetres);
            PutText(ws.Cell(row, c++), visit.WasCapturedOffline ? "Yes" : "No");
            PutText(ws.Cell(row, c++), visit.CheckInNotes);
            PutText(ws.Cell(row, c), visit.CheckOutNotes);

            row++;
            index++;
        }

        FinishVanTable(ws, headerRow, row - 1, lastCol, rows.Count == 0
            ? "No calls match the filters this was exported with."
            : null);

        return WorkbookToBytes(workbook);
    }

    /// <summary>One table under one title bar: filter dropdowns on it, the header frozen, the name columns frozen.</summary>
    private static void FinishVanTable(IXLWorksheet ws, int headerRow, int lastRow, int lastCol, string? emptyNote)
    {
        if (emptyNote is not null)
        {
            ws.Cell(headerRow + 1, 1).Value = emptyNote;
            ws.Range(headerRow + 1, 1, headerRow + 1, lastCol).Merge();
            ws.Cell(headerRow + 1, 1).Style.Font.Italic = true;
            ws.Cell(headerRow + 1, 1).Style.Font.FontColor = TsTextMuted;
        }
        else
        {
            ws.Range(headerRow, 1, lastRow, lastCol).SetAutoFilter();
        }

        TsFinalize(ws, lastCol, freezeRow: headerRow, freezeCol: 2);
    }

    private static string PeriodText(DateTime? fromDate, DateTime? toDate) =>
        (fromDate, toDate) switch
        {
            ({ } from, { } to) when from.Date == to.Date => $"{from:dd MMM yyyy}",
            ({ } from, { } to) => $"{from:dd MMM yyyy} to {to:dd MMM yyyy}",
            _ => "all dates"
        };

    private static string? Clock(DateTime? time) => time?.ToString("HH:mm");

    private static void PutText(IXLCell cell, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            cell.Value = value;
        }
    }

    private static void PutDay(IXLCell cell, DateTime day)
    {
        cell.Value = day.Date;
        cell.Style.NumberFormat.Format = ComplianceDayFormat;
    }

    private static void PutNumber(IXLCell cell, int? value)
    {
        if (value is { } number)
        {
            cell.Value = number;
        }
    }

    private static void PutMoney(IXLCell cell, decimal? value)
    {
        if (value is { } amount)
        {
            cell.Value = amount;
            cell.Style.NumberFormat.Format = ComplianceMoneyFormat;
        }
    }

    private static void PutRate(IXLCell cell, double? rate)
    {
        if (rate is { } value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = ComplianceRateFormat;
        }
    }

    private static void PutWhole(IXLCell cell, double? value)
    {
        if (value is { } number)
        {
            cell.Value = Math.Round(number, 0, MidpointRounding.AwayFromZero);
            cell.Style.NumberFormat.Format = "#,##0";
        }
    }

    private static void PutCoordinate(IXLCell cell, double? value)
    {
        if (value is { } degrees)
        {
            cell.Value = degrees;
            cell.Style.NumberFormat.Format = "0.000000";
        }
    }
}
