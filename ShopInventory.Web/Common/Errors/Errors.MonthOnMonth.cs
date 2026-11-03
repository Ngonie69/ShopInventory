using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class MonthOnMonth
    {
        public static Error LoadFailed(string message) => Error.Failure("MonthOnMonth.LoadFailed", message);
    }

    public static class ReportProductGroup
    {
        public static Error LoadFailed(string message) => Error.Failure("ReportProductGroup.LoadFailed", message);

        public static Error SaveFailed(string message) => Error.Failure("ReportProductGroup.SaveFailed", message);

        public static Error DeleteFailed(string message) => Error.Failure("ReportProductGroup.DeleteFailed", message);
    }
}
