namespace ShopInventory.Common.Crates;

public static class CrateTrackingConstants
{
    public const string TransactionTypeInvoice = "Invoice";
    public const string TransactionTypeOpeningBalance = "OpeningBalance";

    public const string SubmissionRoleDriver = "Driver";
    public const string SubmissionRoleMerchandiser = "Merchandiser";

    public const string StatusPendingDriverPod = "PendingDriverPod";
    public const string StatusPendingMerchandiserPod = "PendingMerchandiserPod";
    public const string StatusMatched = "Matched";
    public const string StatusVariancePendingGrv = "VariancePendingGrv";
    public const string StatusGrvRaised = "GrvRaised";

    public const string AttachmentEntityTypeCrateTransaction = "CrateTransaction";
    public const string AttachmentEntityTypeCratePodSubmission = "CratePodSubmission";
    public const string AttachmentEntityTypeCrateGrv = "CrateGrv";
}