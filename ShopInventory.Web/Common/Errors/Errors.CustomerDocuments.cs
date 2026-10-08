using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Customers' WhatsApp numbers and the documents sent to them. One code: every refusal here is the
    /// API's, and its sentence — which names the number, the invoice or the rule — is what the person needs.
    /// </summary>
    public static class CustomerDocuments
    {
        public static Error Failed(string message) =>
            Error.Failure("CustomerDocuments.Failed", message);
    }
}
