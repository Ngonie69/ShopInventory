using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class FiscalPrintForm
    {
        public static Error Failed(string message) =>
            Error.Failure("FiscalPrintForm.Failed", message);
    }
}
