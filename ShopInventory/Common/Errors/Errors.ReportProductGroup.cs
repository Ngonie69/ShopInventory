using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class ReportProductGroup
    {
        public static Error NotFound(int id) =>
            Error.NotFound("ReportProductGroup.NotFound", $"Product group {id} does not exist.");

        public static Error DuplicateName(string name) =>
            Error.Conflict("ReportProductGroup.DuplicateName", $"A product group called \"{name}\" already exists.");
    }
}
