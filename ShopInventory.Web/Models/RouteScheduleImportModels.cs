namespace ShopInventory.Web.Models;

// The calling-schedule import: the office's "Customer Database per Route" workbook read into the
// weekdays each van calls on each shop. Mirrors ShopInventory/DTOs/RouteScheduleImportDto.cs by hand —
// keep the nullability the same on both sides.

/// <summary>What the workbook said, before the API has matched any of it to a shop.</summary>
public sealed class RouteScheduleWorkbookModel
{
    public List<RouteScheduleSheetModel> Sheets { get; set; } = [];
}

/// <summary>One sheet of the workbook: one truck's week.</summary>
public sealed class RouteScheduleSheetModel
{
    public string Name { get; set; } = string.Empty;

    public List<RouteScheduleOutletModel> Outlets { get; set; } = [];

    /// <summary>
    /// What was on the sheet and was not read, each as a sentence: a column with no weekday heading, a
    /// weekday heading used twice, names found below a gap. Shown so nothing is dropped silently.
    /// </summary>
    public List<string> Skipped { get; set; } = [];
}

/// <summary>One outlet name under one weekday heading.</summary>
public sealed class RouteScheduleOutletModel
{
    /// <summary>Where it is in the workbook — <c>WEST 1!B3</c>. Identifies the row across a check and an apply.</summary>
    public string Ref { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DayOfWeek Day { get; set; }

    /// <summary>The number in the SEQ column beside it, or null when it has none — usually an area heading.</summary>
    public int? Sequence { get; set; }
}

public sealed class RouteScheduleImportRequestModel
{
    /// <summary>True checks and reports; false also saves.</summary>
    public bool ValidateOnly { get; set; } = true;

    public List<RouteScheduleImportSheetModel> Sheets { get; set; } = [];
}

public sealed class RouteScheduleImportSheetModel
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The van account the sheet is for, or null to let the API work it out from the sheet name.</summary>
    public string? AccountCode { get; set; }

    public List<RouteScheduleImportOutletModel> Outlets { get; set; } = [];
}

public sealed class RouteScheduleImportOutletModel
{
    public string Ref { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DayOfWeek Day { get; set; }

    public int? Sequence { get; set; }

    /// <summary>The shop the office picked for this outlet, overriding the automatic match.</summary>
    public int? PickedRouteCustomerId { get; set; }

    /// <summary>The office chose to leave this outlet out.</summary>
    public bool Excluded { get; set; }
}

public sealed class RouteScheduleImportResultModel
{
    public bool Applied { get; set; }

    public List<RouteScheduleTruckOptionModel> Trucks { get; set; } = [];

    public List<RouteScheduleSheetResultModel> Sheets { get; set; } = [];

    public List<RouteScheduleAccountSummaryModel> Accounts { get; set; } = [];
}

public sealed class RouteScheduleTruckOptionModel
{
    public string AccountCode { get; set; } = string.Empty;

    public string? RouteName { get; set; }

    public int ActiveShopCount { get; set; }
}

public sealed class RouteScheduleSheetResultModel
{
    public string Name { get; set; } = string.Empty;

    public string? AccountCode { get; set; }

    /// <summary>True when the API chose the truck from the sheet name rather than being told.</summary>
    public bool AccountGuessed { get; set; }

    public List<RouteScheduleOutletResultModel> Outlets { get; set; } = [];
}

public sealed class RouteScheduleOutletResultModel
{
    public string Ref { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DayOfWeek Day { get; set; }

    public int? Sequence { get; set; }

    /// <summary>See <see cref="RouteScheduleMatchStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    public int? RouteCustomerId { get; set; }

    public string? RouteCustomerCode { get; set; }

    public string? RouteCustomerName { get; set; }

    public List<RouteScheduleCandidateModel> Candidates { get; set; } = [];
}

public sealed class RouteScheduleCandidateModel
{
    public int RouteCustomerId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class RouteScheduleAccountSummaryModel
{
    public string AccountCode { get; set; } = string.Empty;

    public string? RouteName { get; set; }

    public int ActiveShopCount { get; set; }

    public int ScheduledBefore { get; set; }

    public int ScheduledAfter { get; set; }

    public List<RouteScheduleDayCountModel> ShopsPerDay { get; set; } = [];

    public bool Applied { get; set; }

    public string? Note { get; set; }
}

public sealed class RouteScheduleDayCountModel
{
    public DayOfWeek Day { get; set; }

    public int Shops { get; set; }
}

/// <summary>The API's verdict on one outlet, as the strings it sends.</summary>
public static class RouteScheduleMatchStatus
{
    public const string Exact = "Exact";
    public const string Close = "Close";
    public const string Picked = "Picked";
    public const string Ambiguous = "Ambiguous";
    public const string Unmatched = "Unmatched";
    public const string Excluded = "Excluded";
    public const string PickRejected = "PickRejected";
    public const string NoTruck = "NoTruck";

    /// <summary>Whether the outlet will be on the schedule if this is saved.</summary>
    public static bool IsScheduled(string status) => status is Exact or Close or Picked;
}
