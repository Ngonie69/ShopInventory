using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class FiscalisationSettings
    {
        public static Error UpdateFailed(string message) =>
            Error.Failure("FiscalisationSettings.UpdateFailed", message);

        public static Error ApiKeyRequired =>
            Error.Validation(
                "FiscalisationSettings.ApiKeyRequired",
                "An API key is required. Issue one from the Fiscalisation console's API Keys page.");

        /// <summary>
        /// The key was rejected by the platform, so it was not stored. Storing a key we know is refused
        /// would trade a clear error here for a 401 on every document later.
        /// </summary>
        public static Error ApiKeyRejected(string message) =>
            Error.Validation("FiscalisationSettings.ApiKeyRejected", message);
    }
}
