namespace ShopInventory.DTOs;

// The calling-schedule import. The Web reads the office's "Customer Database per Route" workbook into
// these rows; the API matches each outlet name to a shop on the truck and, when told to, saves the
// weekdays as the shops' RouteCustomerVisitDays. Mirrored by hand in
// ShopInventory.Web/Models/RouteScheduleImportModels.cs — keep the nullability the same on both sides.

public sealed class RouteScheduleImportRequest
{
    /// <summary>True checks and reports without saving. The page always checks first.</summary>
    public bool ValidateOnly { get; set; } = true;

    public List<RouteScheduleImportSheet> Sheets { get; set; } = [];
}

/// <summary>One sheet of the workbook: one truck's week.</summary>
public sealed class RouteScheduleImportSheet
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The van account the sheet is for, or null to work it out from the sheet name.</summary>
    public string? AccountCode { get; set; }

    public List<RouteScheduleImportOutlet> Outlets { get; set; } = [];
}

public sealed class RouteScheduleImportOutlet
{
    /// <summary>Where it is in the workbook — <c>WEST 1!B3</c>. Echoed back so the page can line results up.</summary>
    public string Ref { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DayOfWeek Day { get; set; }

    public int? Sequence { get; set; }

    /// <summary>A shop the office picked for this outlet, in place of the automatic match.</summary>
    public int? PickedRouteCustomerId { get; set; }

    /// <summary>The office chose to leave this outlet off the schedule.</summary>
    public bool Excluded { get; set; }
}

public sealed class RouteScheduleImportResultDto
{
    /// <summary>True when at least one truck's schedule was saved.</summary>
    public bool Applied { get; set; }

    /// <summary>Every van account with shops on it, for the page's truck picker.</summary>
    public List<RouteScheduleTruckOptionDto> Trucks { get; set; } = [];

    public List<RouteScheduleSheetResultDto> Sheets { get; set; } = [];

    /// <summary>One line per truck the sheets resolved to — two sheets can share one truck.</summary>
    public List<RouteScheduleAccountSummaryDto> Accounts { get; set; } = [];
}

public sealed class RouteScheduleTruckOptionDto
{
    public string AccountCode { get; set; } = string.Empty;

    public string? RouteName { get; set; }

    public int ActiveShopCount { get; set; }
}

public sealed class RouteScheduleSheetResultDto
{
    public string Name { get; set; } = string.Empty;

    public string? AccountCode { get; set; }

    public bool AccountGuessed { get; set; }

    public List<RouteScheduleOutletResultDto> Outlets { get; set; } = [];
}

public sealed class RouteScheduleOutletResultDto
{
    public string Ref { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DayOfWeek Day { get; set; }

    public int? Sequence { get; set; }

    /// <summary>One of <see cref="Features.RouteCustomers.RouteScheduleMatchStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    public int? RouteCustomerId { get; set; }

    public string? RouteCustomerCode { get; set; }

    public string? RouteCustomerName { get; set; }

    /// <summary>Shops the outlet might be: the duplicates of an ambiguous name, or the nearest spellings.</summary>
    public List<RouteScheduleCandidateDto> Candidates { get; set; } = [];
}

public sealed class RouteScheduleCandidateDto
{
    public int RouteCustomerId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class RouteScheduleAccountSummaryDto
{
    public string AccountCode { get; set; } = string.Empty;

    public string? RouteName { get; set; }

    public int ActiveShopCount { get; set; }

    /// <summary>Active shops on the truck with at least one calling day, before this import.</summary>
    public int ScheduledBefore { get; set; }

    /// <summary>Active shops on the truck with at least one calling day once this import is saved.</summary>
    public int ScheduledAfter { get; set; }

    public List<RouteScheduleDayCountDto> ShopsPerDay { get; set; } = [];

    public bool Applied { get; set; }

    /// <summary>Why the truck was not saved, when it was not.</summary>
    public string? Note { get; set; }
}

public sealed class RouteScheduleDayCountDto
{
    public DayOfWeek Day { get; set; }

    public int Shops { get; set; }
}
