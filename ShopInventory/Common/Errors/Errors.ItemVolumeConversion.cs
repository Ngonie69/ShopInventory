using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class ItemVolumeConversion
    {
        public static Error NotFound(string itemCode) =>
            Error.NotFound(
                "ItemVolumeConversion.NotFound",
                $"No volume conversion factor exists for item {itemCode}.");

        public static Error SaveFailed(string message) =>
            Error.Failure("ItemVolumeConversion.SaveFailed", message);
    }
}
