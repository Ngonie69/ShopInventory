using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Timesheet
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Timesheet.NotFound", $"Timesheet entry with ID {id} not found.");

        public static readonly Error AlreadyCheckedIn =
            Error.Conflict("Timesheet.AlreadyCheckedIn", "You already have an active check-in. Please check out first.");

        public static readonly Error NoActiveCheckIn =
            Error.Validation("Timesheet.NoActiveCheckIn", "No active check-in found to check out from.");

        public static Error CheckOutFailed(string message) =>
            Error.Failure("Timesheet.CheckOutFailed", message);

        public static Error CheckInFailed(string message) =>
            Error.Failure("Timesheet.CheckInFailed", message);

        public static readonly Error Unauthorized =
            Error.Unauthorized("Timesheet.Unauthorized", "User is not authenticated.");
    }
}
