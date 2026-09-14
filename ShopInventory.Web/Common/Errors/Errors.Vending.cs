using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class Vending
    {
        public static readonly Error FileUnreadable =
            Error.Validation("Vending.FileUnreadable", "That file could not be read as an Excel workbook. Save it as .xlsx and upload it again.");

        public static readonly Error NotTheTemplate =
            Error.Validation("Vending.NotTheTemplate", "That file is not the vendor template: it has no First name column beside a Vendor code or Depot column. Download the template and fill that in.");

        public static readonly Error FileHasNoVendors =
            Error.Validation("Vending.FileHasNoVendors", "The file has no vendors in it. Fill them in from row 2 of the Vendors sheet.");

        public static Error FileTooLong(int rowCount, int maxRows) =>
            Error.Validation("Vending.FileTooLong", $"The file has {rowCount} vendors; upload at most {maxRows} at a time.");

        public static Error TemplateFailed(string message) =>
            Error.Failure("Vending.TemplateFailed", message);

        public static Error ImportFailed(string message) =>
            Error.Failure("Vending.ImportFailed", message);
    }
}
