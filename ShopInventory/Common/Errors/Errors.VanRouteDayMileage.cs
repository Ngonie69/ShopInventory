using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Refusals when an Admin corrects the odometer readings on a rep's trading day.
    /// </summary>
    public static class VanRouteDayMileage
    {
        public static Error DayNotFound =>
            Error.NotFound(
                "VanRouteDayMileage.DayNotFound",
                "That trading day no longer exists.");

        public static Error ReasonRequired =>
            Error.Validation(
                "VanRouteDayMileage.ReasonRequired",
                "Say why the readings are being corrected. The reason is kept with the correction.");

        public static Error ReadingOutOfRange(int maximum) =>
            Error.Validation(
                "VanRouteDayMileage.ReadingOutOfRange",
                $"An odometer reading has to be between 0 and {maximum:N0}.");

        public static Error ClosingBeforeStarting(int starting, int closing) =>
            Error.Validation(
                "VanRouteDayMileage.ClosingBeforeStarting",
                $"The reading in ({closing:N0}) cannot be less than the reading out ({starting:N0}).");

        /// <summary>
        /// The rep enters the closing reading when they end the day, and End Day writes over whatever
        /// is there. A closing reading put on an open day would be overwritten within hours, silently.
        /// </summary>
        public static Error DayStillOpen =>
            Error.Validation(
                "VanRouteDayMileage.DayStillOpen",
                "This day is still open. The reading in is entered by the rep when they end the day; only the reading out can be corrected until then.");

        public static Error NothingChanged =>
            Error.Validation(
                "VanRouteDayMileage.NothingChanged",
                "Those are the readings the day already holds.");

        /// <summary>
        /// The page was read before the readings it would replace were changed — usually by the rep
        /// ending the day. Saving would put back the old figures under the new ones.
        /// </summary>
        public static Error Stale(int? starting, int? closing) =>
            Error.Conflict(
                "VanRouteDayMileage.Stale",
                $"The readings changed since this page was loaded: they are now out {Show(starting)}, in {Show(closing)}. Reload and correct them again.");

        private static string Show(int? reading) => reading is { } value ? value.ToString("N0") : "not entered";
    }
}
