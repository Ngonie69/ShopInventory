using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Failures of the SAP approval procedure flow for A/R credit memo drafts: listing what SAP holds,
    /// recording a decision as the service approver, and adding the approved draft.
    /// </summary>
    public static class CreditNoteApproval
    {
        public static readonly Error SapDisabled =
            Error.Failure("CreditNoteApproval.SapDisabled", "SAP integration is disabled, so held credit notes cannot be read or decided.");

        public static Error NotFound(int code) =>
            Error.NotFound("CreditNoteApproval.NotFound", $"SAP approval request {code} was not found, or it is not for an A/R credit memo.");

        public static Error NoDraft(int code) =>
            Error.Validation("CreditNoteApproval.NoDraft", $"SAP approval request {code} holds no draft document, so there is nothing to add.");

        public static Error DraftMissing(int draftEntry) =>
            Error.NotFound("CreditNoteApproval.DraftMissing", $"The draft document {draftEntry} behind this approval request no longer exists in SAP.");

        public static Error NotACreditNoteDraft(int draftEntry) =>
            Error.Validation("CreditNoteApproval.NotACreditNoteDraft", $"Draft {draftEntry} is not an A/R credit memo.");

        public static Error InvalidDecision(string? decision) =>
            Error.Validation("CreditNoteApproval.InvalidDecision", $"'{decision}' is not a decision. Use Approved or NotApproved.");

        public static Error NotPending(string status) =>
            Error.Validation("CreditNoteApproval.NotPending", $"This approval request is {status}; only a pending request can be decided.");

        public static readonly Error NoCurrentStage =
            Error.Validation("CreditNoteApproval.NoCurrentStage", "SAP has not assigned this request a stage yet, so there is nothing to decide.");

        public static Error NotApproved(string status) =>
            Error.Validation("CreditNoteApproval.NotApproved", $"This approval request is {status}; only an approved draft can be added.");

        public static readonly Error DraftNotOpen =
            Error.Validation("CreditNoteApproval.DraftNotOpen", "The draft is closed or cancelled in SAP and cannot be added.");

        public static Error AlreadyAdded(int code, int? objectEntry) =>
            Error.Conflict("CreditNoteApproval.AlreadyAdded",
                objectEntry.HasValue
                    ? $"Approval request {code} has already been added as credit note DocEntry {objectEntry}."
                    : $"Approval request {code} has already been added.");

        public static Error ApproverUnknown(string approverUserName) =>
            Error.Validation("CreditNoteApproval.ApproverUnknown",
                $"SAP has no user '{approverUserName}' to record the decision as. Check SAP:ApprovalApproverUsername.");

        public static Error ApproverPasswordMissing(string approverUserName) =>
            Error.Failure("CreditNoteApproval.ApproverPasswordMissing",
                $"SAP:ApprovalApproverUsername names '{approverUserName}' but no password is configured for it, " +
                "and SAP will not record a decision for a named approver without one. " +
                "Set SAP:ApprovalApproverPassword, or clear the username to decide as the app's own SAP account.");

        public static Error ApproverNotOnStage(string stageName, string approverUserName) =>
            Error.Validation("CreditNoteApproval.ApproverNotOnStage",
                $"SAP stage '{stageName}' does not list {approverUserName} as an approver, so this app cannot decide it. " +
                $"Ask the SAP administrator to add {approverUserName} to that stage.");

        public static Error AlreadyDecided(string stageName) =>
            Error.Conflict("CreditNoteApproval.AlreadyDecided", $"A decision has already been recorded on stage '{stageName}'.");

        public static Error SapRejected(string message) =>
            Error.Failure("CreditNoteApproval.SapRejected", $"SAP refused the request: {message}");

        public static Error SapUnavailable(string message) =>
            Error.Failure("CreditNoteApproval.SapUnavailable", $"SAP could not be reached, and nothing was recorded: {message}");

        public static readonly Error Cancelled =
            Error.Failure("CreditNoteApproval.Cancelled", "The request was cancelled before anything reached SAP.");

        public static readonly Error DecisionInProgress =
            Error.Conflict("CreditNoteApproval.DecisionInProgress", "This decision is already being recorded. Wait for it to finish, then reload.");

        public static readonly Error DecisionUncertain =
            Error.Failure("CreditNoteApproval.DecisionUncertain",
                "SAP did not answer in time. The decision may have been recorded — reload the request from SAP before deciding again.");

        public static readonly Error AddInProgress =
            Error.Conflict("CreditNoteApproval.AddInProgress", "This draft is already being added. Wait for it to finish, then reload.");

        public static readonly Error AddUncertain =
            Error.Failure("CreditNoteApproval.AddUncertain",
                "SAP did not answer in time. The credit note may already exist — check the request in SAP before adding again.");

        public static Error AttachmentNotFound(int code, int lineNum) =>
            Error.NotFound("CreditNoteApproval.AttachmentNotFound", $"Approval request {code} has no attachment line {lineNum}.");

        public static Error AttachmentUnavailable(string message) =>
            Error.Failure("CreditNoteApproval.AttachmentUnavailable", $"The attachment could not be read from SAP: {message}");
    }
}
