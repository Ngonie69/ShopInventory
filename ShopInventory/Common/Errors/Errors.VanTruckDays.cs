using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Failures on the daily truck report and on what the office enters for a truck's day.
    /// </summary>
    public static class VanTruckDays
    {
        public static Error InvalidRange =>
            Error.Validation(
                "VanTruckDays.InvalidRange",
                "The end of the period cannot be before its start.");

        public static Error RangeTooWide(int maximumDays) =>
            Error.Validation(
                "VanTruckDays.RangeTooWide",
                $"Choose a period of {maximumDays} days or fewer.");

        public static Error RouteNotFound =>
            Error.NotFound(
                "VanTruckDays.RouteNotFound",
                "That route no longer exists.");

        /// <summary>
        /// A figure for tomorrow is a typo in the date, and saving it would put fuel on a day that has
        /// not happened and leave the day it was meant for empty.
        /// </summary>
        public static Error DayInFuture =>
            Error.Validation(
                "VanTruckDays.DayInFuture",
                "That day has not happened yet. Check the date.");

        public static Error FuelOutOfRange(decimal maximumLitres) =>
            Error.Validation(
                "VanTruckDays.FuelOutOfRange",
                $"Fuel issued has to be between 0 and {maximumLitres:0} litres.");

        public static Error AccountSalesNegative =>
            Error.Validation(
                "VanTruckDays.AccountSalesNegative",
                "Account sales cannot be negative.");
    }
}
