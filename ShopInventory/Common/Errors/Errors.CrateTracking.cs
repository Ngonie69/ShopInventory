using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class CrateTracking
    {
        public static Error TransactionNotFound(int id) =>
            Error.NotFound("CrateTracking.TransactionNotFound", $"Crate transaction {id} was not found.");

        public static Error InvoiceNotFound(int docNum) =>
            Error.NotFound("CrateTracking.InvoiceNotFound", $"Invoice {docNum} was not found or is not available for crate tracking.");

        public static Error InvoiceDocEntryNotFound(int docEntry) =>
            Error.NotFound("CrateTracking.InvoiceDocEntryNotFound", $"Invoice doc entry {docEntry} was not found or is not available for crate tracking.");

        public static Error SubmissionNotFound(int id) =>
            Error.NotFound("CrateTracking.SubmissionNotFound", $"Crate POD submission {id} was not found.");

        public static Error GrvNotFound(int id) =>
            Error.NotFound("CrateTracking.GrvNotFound", $"Crate GRV {id} was not found.");

        public static Error InvalidQuantity(string message) =>
            Error.Validation("CrateTracking.InvalidQuantity", message);

        public static Error InvalidShop(string message) =>
            Error.Validation("CrateTracking.InvalidShop", message);

        public static Error InvalidSubmissionRole =>
            Error.Validation("CrateTracking.InvalidSubmissionRole", "Submission role must be Driver or Merchandiser.");

        public static Error InvalidTransactionType(string message) =>
            Error.Validation("CrateTracking.InvalidTransactionType", message);

        public static Error AccessDenied(string message) =>
            Error.Forbidden("CrateTracking.AccessDenied", message);

        public static Error OpeningBalanceDocumentRequired =>
            Error.Validation("CrateTracking.OpeningBalanceDocumentRequired", "Opening balances require an accompanying document.");

        public static Error PodDocumentRequired =>
            Error.Validation("CrateTracking.PodDocumentRequired", "Crate POD uploads require an accompanying document.");

        public static Error GrvDocumentRequired =>
            Error.Validation("CrateTracking.GrvDocumentRequired", "Crate GRVs require an accompanying document.");

        public static Error MerchandiserPodRequired =>
            Error.Validation("CrateTracking.MerchandiserPodRequired", "A merchandiser POD must be uploaded before raising a GRV.");

        public static Error NoVarianceForGrv =>
            Error.Validation("CrateTracking.NoVarianceForGrv", "A GRV can only be raised when the merchandiser quantity differs from the expected crate quantity.");

        public static Error GrvAlreadyExists(int transactionId) =>
            Error.Conflict("CrateTracking.GrvAlreadyExists", $"A GRV already exists for crate transaction {transactionId}.");
    }
}