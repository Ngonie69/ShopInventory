using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class AppVersion
    {
        public static Error SettingsUpdateFailed(string message) =>
            Error.Failure("AppVersion.SettingsUpdateFailed", message);
    }
}