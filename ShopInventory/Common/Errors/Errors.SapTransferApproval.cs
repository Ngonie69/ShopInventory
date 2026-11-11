using ErrorOr;
using ShopInventory.Models;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Failures of adding an inventory transfer or transfer request that was keyed in the SAP client and
    /// approved by SAP's approval procedure: reading the approved drafts, and turning one into the real
    /// document.
    /// </summary>
    public static class SapTransferApproval
    {
        /// <summary>What the draft becomes, in the words a message uses for it.</summary>
        private static string Noun(SapHeldDraftKind kind) =>
            kind == SapHeldDraftKind.Request ? "transfer request" : "transfer";

        public static readonly Error SapDisabled =
            Error.Failure("SapTransferApproval.SapDisabled", "SAP integration is disabled, so held transfers cannot be read or added.");

        public static Error NotFound(int draftEntry) =>
            Error.NotFound("SapTransferApproval.NotFound", $"SAP holds no inventory transfer or transfer request draft {draftEntry}.");

        public static readonly Error RoleNotAllowed =
            Error.Forbidden("SapTransferApproval.RoleNotAllowed",
                "Only administrators, stock controllers, the wash bay, depot controllers and shops' stock users add transfers SAP is holding.");

        public static readonly Error NoAssignedWarehouses =
            Error.Forbidden("SapTransferApproval.NoAssignedWarehouses",
                "Your account has no warehouses assigned, so there are no held transfers for you to add.");

        public static Error OutsideWarehouseScope(string warehouse, SapHeldDraftKind kind = SapHeldDraftKind.Transfer) =>
            Error.Forbidden("SapTransferApproval.OutsideWarehouseScope",
                kind == SapHeldDraftKind.Request
                    ? $"This transfer request asks for stock from {warehouse}, which is not one of your warehouses."
                    : $"This transfer takes stock out of {warehouse}, which is not one of your warehouses.");

        public static Error NotApproved(string state, SapHeldDraftKind kind = SapHeldDraftKind.Transfer) =>
            Error.Validation("SapTransferApproval.NotApproved",
                $"SAP has this {Noun(kind)} draft as {state}; only an approved draft can be added.");

        public static Error ApprovalNotSettled(int code, string status, SapHeldDraftKind kind = SapHeldDraftKind.Transfer) =>
            Error.Validation("SapTransferApproval.ApprovalNotSettled",
                $"SAP approval request {code} on this {Noun(kind)} is still {status}, so it cannot be added yet.");

        public static Error AlreadyAdded(int draftNum, int? transferDocNum, IReadOnlyCollection<string> openRequests)
        {
            var added = transferDocNum is int docNum
                ? $"Transfer draft {draftNum} has already been added as inventory transfer #{docNum}."
                : $"Transfer draft {draftNum} has already been added in SAP.";
            var open = openRequests.Count == 0
                ? string.Empty
                : $" {string.Join(" ", openRequests)}";
            return Error.Conflict("SapTransferApproval.AlreadyAdded", added + open);
        }

        /// <summary>A request draft that is closed: added already, here or in the B1 client.</summary>
        public static Error AlreadyAddedRequest(int draftNum, int? requestDocNum) =>
            Error.Conflict("SapTransferApproval.AlreadyAdded",
                requestDocNum is int docNum
                    ? $"Request draft {draftNum} has already been added as transfer request #{docNum}."
                    : $"Request draft {draftNum} has already been added in SAP.");

        /// <summary>
        /// A batch- or serial-managed line has no complete pick. SAP refuses the whole transfer for one
        /// such line, so the add is refused here with which lines, before anything reaches SAP.
        /// </summary>
        public static Error SelectionMissing(IReadOnlyCollection<string> lines) =>
            Error.Validation("SapTransferApproval.SelectionMissing",
                $"SAP will not add this transfer: {string.Join("; ", lines)}. Nothing was transferred. " +
                "Pick the batches on the draft in SAP Business One, then add it.");

        /// <summary>SAP itself refused for a missing batch or serial pick — the same fix, in SAP's words.</summary>
        public static Error SelectionRefusedBySap(string sapMessage) =>
            Error.Validation("SapTransferApproval.SelectionMissing",
                $"SAP will not add this transfer because a batch or serial number is not picked on every line ({sapMessage}). " +
                "Nothing was transferred. Pick the batches on the draft in SAP Business One, then add it.");

        public static Error AlreadyTransferredByApp(int requestDocNum, int transferDocNum) =>
            Error.Conflict("SapTransferApproval.AlreadyTransferredByApp",
                $"Transfer request #{requestDocNum}, which this draft was copied from, was already converted here into " +
                $"inventory transfer #{transferDocNum}. Adding the draft would move the same stock a second time, so nothing was added.");

        public static Error ConversionInProgress(int requestDocEntry) =>
            Error.Conflict("SapTransferApproval.ConversionInProgress",
                $"Transfer request {requestDocEntry}, which this draft was copied from, is being converted right now. " +
                "Wait for that to finish, then reload.");

        public static Error TillSalesWouldBeTaken(string detail) =>
            Error.Validation("SapTransferApproval.TillSalesWouldBeTaken",
                $"This transfer was not added, and nothing was transferred: {detail}. " +
                "Till sales normally reach SAP within a minute, so try again shortly.");

        public static Error StockUnreadable(string warehouses) =>
            Error.Failure("SapTransferApproval.StockUnreadable",
                $"Could not read stock from SAP for {warehouses}, so this transfer cannot be checked against sales the " +
                "tills have made that SAP does not have yet. Nothing was transferred; try again in a minute.");

        public static Error SapRejected(string message, SapHeldDraftKind kind = SapHeldDraftKind.Transfer) =>
            Error.Failure("SapTransferApproval.SapRejected", $"SAP refused the {Noun(kind)}, and nothing was added: {message}");

        public static Error SapUnavailable(string message) =>
            Error.Failure("SapTransferApproval.SapUnavailable", $"SAP could not be reached, and nothing was added: {message}");

        public static readonly Error Cancelled =
            Error.Failure("SapTransferApproval.Cancelled", "The request was cancelled before anything reached SAP.");

        public static readonly Error AddInProgress =
            Error.Conflict("SapTransferApproval.AddInProgress", "This transfer is already being added. Wait for it to finish, then reload.");

        public static readonly Error AddUncertain = AddUncertainFor(SapHeldDraftKind.Transfer);

        public static Error AddUncertainFor(SapHeldDraftKind kind) =>
            Error.Failure("SapTransferApproval.AddUncertain",
                $"SAP did not answer in time. The {Noun(kind)} may already exist — reload before adding again; " +
                "a draft that was added no longer appears in the list.");
    }
}
