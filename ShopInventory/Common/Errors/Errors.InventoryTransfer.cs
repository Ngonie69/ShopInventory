using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class InventoryTransfer
    {
        public static readonly Error SapDisabled =
            Error.Failure("InventoryTransfer.SapDisabled", "SAP integration is disabled.");

        public static Error NotFound(int docEntry) =>
            Error.NotFound("InventoryTransfer.NotFound", $"Inventory transfer with DocEntry {docEntry} not found.");

        public static Error TransferRequestNotFound(int docEntry) =>
            Error.NotFound("InventoryTransfer.TransferRequestNotFound", $"Transfer request with DocEntry {docEntry} not found.");

        public static Error ValidationFailed(string message) =>
            Error.Validation("InventoryTransfer.ValidationFailed", message);

        public static Error InvalidWarehouse(string message) =>
            Error.Validation("InventoryTransfer.InvalidWarehouse", message);

        public static Error InsufficientStock(string message) =>
            Error.Validation("InventoryTransfer.InsufficientStock", message);

        public static Error NegativeStock(string message) =>
            Error.Validation("InventoryTransfer.NegativeStock", $"Transfer rejected: {message}");

        public static Error SapTimeout =>
            Error.Failure("InventoryTransfer.SapTimeout", "Connection to SAP Service Layer timed out or was aborted.");

        public static Error SapConnectionError(string message) =>
            Error.Failure("InventoryTransfer.SapConnectionError", $"Unable to connect to SAP Service Layer. {message}");

        public static Error CreationFailed(string message) =>
            Error.Failure("InventoryTransfer.CreationFailed", message);

        public static Error InvalidDateFormat(string paramName) =>
            Error.Validation("InventoryTransfer.InvalidDateFormat", $"Invalid {paramName} format. Use yyyy-MM-dd format.");

        public static Error InvalidDateRange =>
            Error.Validation("InventoryTransfer.InvalidDateRange", "fromDate cannot be greater than toDate.");

        public static Error WarehouseCodeRequired =>
            Error.Validation("InventoryTransfer.WarehouseCodeRequired", "Warehouse code is required.");

        public static Error InvalidOperation(string message) =>
            Error.Validation("InventoryTransfer.InvalidOperation", message);

        public static readonly Error ApproverNotAuthenticated =
            Error.Unauthorized("InventoryTransfer.ApproverNotAuthenticated", "The approver is not authenticated.");

        public static readonly Error TransferRequestConverterRoleRequired =
            Error.Forbidden(
                "InventoryTransfer.TransferRequestConverterRoleRequired",
                "Only an administrator, stock controller, or depot controller can convert a transfer request.");

        public static Error ApproverRoleRequired(string roleName) =>
            Error.Forbidden("InventoryTransfer.ApproverRoleRequired", $"This transfer request must be approved by a {roleName}.");

        public static readonly Error ApprovalRequiresAdminReview =
            Error.Forbidden("InventoryTransfer.ApprovalRequiresAdminReview", "The request creator could not be verified. An administrator must review this transfer request.");

        public static Error ApproverWarehouseRequired(string warehouseCode) =>
            Error.Forbidden("InventoryTransfer.ApproverWarehouseRequired", $"You can only approve transfer requests for your assigned warehouse. Required warehouse: {warehouseCode}.");

        public static readonly Error SelfApprovalNotAllowed =
            Error.Forbidden("InventoryTransfer.SelfApprovalNotAllowed", "You cannot approve or reject your own transfer request.");

        public static Error ApprovalAlreadyDecided(string status) =>
            Error.Conflict("InventoryTransfer.ApprovalAlreadyDecided", $"This transfer request has already been {status}.");

        public static readonly Error ApprovalInProgress =
            Error.Conflict("InventoryTransfer.ApprovalInProgress", "Another approval decision is already being processed for this transfer request.");

        public static Error PendingTransferNotFound(Guid id) =>
            Error.NotFound("InventoryTransfer.PendingTransferNotFound", $"Pending inventory transfer {id} was not found.");

        public static Error PendingTransferNotActionable(string status) =>
            Error.Conflict("InventoryTransfer.PendingTransferNotActionable", $"This inventory transfer is {status} and can no longer be actioned.");

        public static Error WarehouseNotAssigned(string warehouseCode) =>
            Error.Forbidden(
                "InventoryTransfer.WarehouseNotAssigned",
                $"You are not assigned to source warehouse {warehouseCode}, so you cannot action this transfer.");

        /// <summary>
        /// Refuses a warehouse the caller picked for a document rather than one they were asked to
        /// action. Shares <c>WarehouseNotAssigned</c> so clients keep one code for both refusals.
        /// </summary>
        public static Error WarehouseNotAssignable(string warehouseCode) =>
            Error.Forbidden(
                "InventoryTransfer.WarehouseNotAssigned",
                $"You are not assigned to warehouse {warehouseCode}, so you cannot put it on this transfer request. " +
                "Choose one of your assigned warehouses.");

        public static readonly Error NoAssignedWarehouses =
            Error.Forbidden(
                "InventoryTransfer.NoAssignedWarehouses",
                "Your account has no assigned warehouses. Ask an administrator to assign one before actioning transfers.");

        public static Error PendingEditNotFound(Guid id) =>
            Error.NotFound("InventoryTransfer.PendingEditNotFound", $"Pending transfer request change {id} was not found.");

        public static Error PendingEditNotActionable(string status) =>
            Error.Conflict("InventoryTransfer.PendingEditNotActionable", $"This change is {status} and can no longer be actioned.");

        public static readonly Error TransferRequestNotEditable =
            Error.Conflict(
                "InventoryTransfer.TransferRequestNotEditable",
                "This transfer request is closed and can no longer be changed.");

        /// <summary>
        /// SAP would not close the request. <paramref name="decisionRecorded"/> says whether the
        /// local rejection was already written, because that is the difference between "nothing
        /// happened" and "we turned it down but SAP still has it open".
        /// </summary>
        public static Error TransferRequestCloseFailed(int docEntry, string reason, bool decisionRecorded = false) =>
            Error.Failure(
                "InventoryTransfer.TransferRequestCloseFailed",
                (decisionRecorded
                    ? $"The rejection was recorded, but transfer request {docEntry} could not be closed in SAP and is still open. "
                    : $"Transfer request {docEntry} could not be closed in SAP. ") + reason);

        public static Error TransferRequestEditInFlight(int docEntry) =>
            Error.Conflict(
                "InventoryTransfer.TransferRequestEditInFlight",
                $"A change to transfer request {docEntry} is already waiting for approval.");
    }
}
