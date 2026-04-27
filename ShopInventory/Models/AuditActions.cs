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

    // Payment actions
    public const string CreatePayment = "CreatePayment";
    public const string InitiatePayment = "InitiatePayment";
    public const string CancelPayment = "CancelPayment";
    public const string RefundPayment = "RefundPayment";

    // Credit Note actions
    public const string CreateCreditNote = "CreateCreditNote";
    public const string ApproveCreditNote = "ApproveCreditNote";
    public const string DeleteCreditNote = "DeleteCreditNote";

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

    // Inventory Transfer actions
    public const string CreateTransfer = "CreateTransfer";
    public const string CreateTransferRequest = "CreateTransferRequest";
    public const string ConvertTransferRequest = "ConvertTransferRequest";
    public const string CloseTransferRequest = "CloseTransferRequest";

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

    // Customer Portal actions
    public const string RegisterCustomer = "RegisterCustomer";
    public const string BulkRegisterCustomers = "BulkRegisterCustomers";

    // Timesheet actions
    public const string CheckIn = "CheckIn";
    public const string CheckOut = "CheckOut";
}
