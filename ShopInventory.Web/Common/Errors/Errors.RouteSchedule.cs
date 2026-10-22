using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class RouteSchedule
    {
        public static readonly Error FileUnreadable =
            Error.Validation("RouteSchedule.FileUnreadable", "That file could not be read as an Excel workbook. Save it as .xlsx and upload it again.");

        public static readonly Error NoWeekdays =
            Error.Validation("RouteSchedule.NoWeekdays", "No sheet in that workbook has a weekday heading (MONDAY, TUESDAY …) above a list of outlets, so there is no calling schedule to read.");

        public static Error ImportFailed(string message) =>
            Error.Failure("RouteSchedule.ImportFailed", message);
    }
}
