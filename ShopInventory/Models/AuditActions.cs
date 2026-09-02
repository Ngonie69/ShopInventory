namespace ShopInventory.Models;

/// <summary>
/// Common audit action types for API-side operations
/// </summary>
public static class AuditActions
{
    // Authentication
    public const string Login = "Login";
    public const string Logout = "Logout";
    public const string LoginFailed = "LoginFailed";
    public const string MobileBiometricLogin = "MobileBiometricLogin";
    public const string MobileBiometricLoginFailed = "MobileBiometricLoginFailed";
    public const string EnableMobileBiometricLogin = "EnableMobileBiometricLogin";
    public const string DisableMobileBiometricLogin = "DisableMobileBiometricLogin";
    public const string PasskeyLogin = "PasskeyLogin";
    public const string PasskeyLoginFailed = "PasskeyLoginFailed";
    public const string RegisterPasskey = "RegisterPasskey";
    public const string RefreshToken = "RefreshToken";
    public const string RegisterUser = "RegisterUser";

    // Invoice actions
    public const string CreateInvoice = "CreateInvoice";
    public const string FiscalizeInvoice = "FiscalizeInvoice";
    public const string ViewInvoices = "ViewInvoices";

    // Payment actions
    public const string CreatePayment = "CreatePayment";
    public const string InitiatePayment = "InitiatePayment";
    public const string CancelPayment = "CancelPayment";
    public const string RefundPayment = "RefundPayment";

    // Credit Note actions
    public const string CreateCreditNote = "CreateCreditNote";
    public const string ApproveCreditNote = "ApproveCreditNote";
    public const string DeleteCreditNote = "DeleteCreditNote";
    public const string BulkCancelCreditNotes = "BulkCancelCreditNotes";
    public const string DuplicateCancelledCreditNotes = "DuplicateCancelledCreditNotes";

    // SAP-held credit note approvals (the B1 approval procedure, decided from here)
    public const string ApproveSapCreditNote = "ApproveSapCreditNote";
    public const string RejectSapCreditNote = "RejectSapCreditNote";
    public const string AddApprovedCreditNote = "AddApprovedCreditNote";
    public const string ViewCreditNoteDraftAttachment = "ViewCreditNoteDraftAttachment";

    // Sales Order actions
    public const string CreateSalesOrder = "CreateSalesOrder";
    public const string CreateMobileSalesOrder = "Create Mobile Sales Order";
    public const string UpdateSalesOrder = "UpdateSalesOrder";
    public const string ApproveSalesOrder = "ApproveSalesOrder";
    public const string PostSalesOrderToSAP = "PostSalesOrderToSAP";
    public const string ConvertOrderToInvoice = "ConvertOrderToInvoice";
    public const string DeleteSalesOrder = "DeleteSalesOrder";

    // Mobile merchandiser actions
    public const string ViewMobileCategories = "View Mobile Categories";
    public const string ViewMobileProducts = "View Mobile Products";
    public const string ViewMobileCustomerProducts = "View Mobile Customer Products";
    public const string ViewMobileOrders = "View Mobile Orders";

    // Purchase Order actions
    public const string CreatePurchaseOrder = "CreatePurchaseOrder";
    public const string UpdatePurchaseOrder = "UpdatePurchaseOrder";
    public const string ApprovePurchaseOrder = "ApprovePurchaseOrder";
    public const string ReceiveGoods = "ReceiveGoods";
    public const string DeletePurchaseOrder = "DeletePurchaseOrder";
    public const string UploadPurchaseOrderDocument = "UploadPurchaseOrderDocument";

    // Document management actions
    public const string CreateDocumentTemplate = "CreateDocumentTemplate";
    public const string UpdateDocumentTemplate = "UpdateDocumentTemplate";
    public const string DeleteDocumentTemplate = "DeleteDocumentTemplate";
    public const string SetDefaultDocumentTemplate = "SetDefaultDocumentTemplate";
    public const string UploadDocumentAttachment = "UploadDocumentAttachment";
    public const string DeleteDocumentAttachment = "DeleteDocumentAttachment";
    public const string UploadPod = "UploadPod";
    public const string RegisterInvoiceCrates = "RegisterInvoiceCrates";
    public const string CreateCrateOpeningBalance = "CreateCrateOpeningBalance";
    public const string UpdateCrateOpeningBalance = "UpdateCrateOpeningBalance";
    public const string DeleteCrateOpeningBalance = "DeleteCrateOpeningBalance";
    public const string UploadCratePod = "UploadCratePod";
    public const string DeleteCratePod = "DeleteCratePod";
    public const string CreateCrateGrv = "CreateCrateGrv";

    // Inventory Transfer actions
    public const string CreateTransfer = "CreateTransfer";
    public const string CreateTransferRequest = "CreateTransferRequest";
    public const string ConvertTransferRequest = "ConvertTransferRequest";
    public const string CloseTransferRequest = "CloseTransferRequest";
    public const string EditTransferRequest = "EditTransferRequest";
    public const string SubmitTransferRequestEditForApproval = "SubmitTransferRequestEditForApproval";
    public const string ApproveTransferRequestEditStage = "ApproveTransferRequestEditStage";
    public const string RejectTransferRequestEditStage = "RejectTransferRequestEditStage";
    public const string ApproveTransferRequestStage = "ApproveTransferRequestStage";
    public const string RejectTransferRequestStage = "RejectTransferRequestStage";
    public const string SubmitTransferForApproval = "SubmitTransferForApproval";
    public const string ApproveTransferStage = "ApproveTransferStage";
    public const string RejectTransferStage = "RejectTransferStage";
    public const string CancelPendingTransfer = "CancelPendingTransfer";

    // User Management actions
    public const string CreateUser = "CreateUser";
    public const string UpdateUser = "UpdateUser";
    public const string DeleteUser = "DeleteUser";
    public const string ChangePassword = "ChangePassword";
    public const string UnlockUser = "UnlockUser";
    public const string DeactivateUser = "DeactivateUser";
    public const string ActivateUser = "ActivateUser";
    public const string UpdatePermissions = "UpdatePermissions";
    public const string ResetTwoFactor = "ResetTwoFactor";

    // Backup actions
    public const string CreateBackup = "CreateBackup";
    public const string RestoreBackup = "RestoreBackup";
    public const string DeleteBackup = "DeleteBackup";
    public const string ResetDatabase = "ResetDatabase";

    // Settings actions
    public const string UpdateSAPSettings = "UpdateSAPSettings";
    public const string UpdateMobileVersionPolicy = "UpdateMobileVersionPolicy";
    public const string UpdateFiscalisationSettings = "UpdateFiscalisationSettings";

    // Timesheet actions
    public const string CheckIn = "CheckIn";
    public const string CheckOut = "CheckOut";
    public const string ViewAssignedCustomers = "ViewAssignedCustomers";

    // Van sales offline ingest
    //
    // A van's backlog arrives already sold and already fiscalised, so these are the only record of what
    // reached the server. The two per-sale actions below cover the outcomes that leave nothing behind
    // anywhere else: a rejected sale is never stored, and a sale whose receipt cannot be submitted to
    // ZIMRA is stored but will quietly go missing from the fiscal day unless someone is told.
    public const string IngestVanSalesOfflineBatch = "IngestVanSalesOfflineBatch";
    public const string RejectVanSalesOfflineSale = "RejectVanSalesOfflineSale";
    public const string UnsignableVanSalesOfflineSale = "UnsignableVanSalesOfflineSale";

    // Van sales customer app sign-ins
    //
    // Granting or withdrawing one of these decides who may place orders in a shop's name, and it is
    // done in the field on a rep's say-so rather than by anyone in the office. The audit row is the
    // only place that decision is recorded.
    public const string CreateVanSalesCustomerAccount = "CreateVanSalesCustomerAccount";
    public const string DeactivateVanSalesCustomerAccount = "DeactivateVanSalesCustomerAccount";

    // Orders a van sales customer placed for themselves. Auto-accepted, so the audit row is the
    // only place a human decision is recorded — there is no approval step to look back at.
    public const string SubmitVanSalesCustomerOrder = "SubmitVanSalesCustomerOrder";
    public const string CancelVanSalesCustomerOrder = "CancelVanSalesCustomerOrder";
    public const string RecordVanSalesCustomerOrderDelivery = "RecordVanSalesCustomerOrderDelivery";

    // The moment a customer's order becomes a document in the ERP. The only crossing between the
    // standalone intake and the SAP-bound pipeline, and always a person's decision.
    public const string ConvertVanSalesCustomerOrder = "ConvertVanSalesCustomerOrder";
}
