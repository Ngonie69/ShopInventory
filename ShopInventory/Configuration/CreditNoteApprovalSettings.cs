namespace ShopInventory.Configuration;

/// <summary>
/// Settings for approving, adding and reading the attachments of A/R credit memo drafts that SAP's own
/// approval procedure is holding. The approver identity lives on <see cref="SAPSettings"/>, next to the
/// session credentials it defaults to.
/// </summary>
public sealed class CreditNoteApprovalSettings
{
    public const string SectionName = "CreditNoteApprovals";

    /// <summary>
    /// Fiscalise the credit note through the fiscalisation platform right after an approved draft is
    /// added, the way credit notes this app creates are. A document added through the Service Layer
    /// is invisible to the platform's B1 print bridge, so with this off it is fiscalised only when
    /// somebody next prints it from the B1 client.
    /// </summary>
    public bool FiscaliseAfterAdd { get; set; } = true;

    /// <summary>
    /// Where the bytes of a draft's attachment are read from. <c>ServiceLayer</c> streams
    /// <c>Attachments2(n)/$value</c>; <c>Share</c> opens the file on <see cref="SAPSettings.AttachmentsPath"/>
    /// with the share credentials, for a Service Layer that cannot reach the attachments folder.
    /// </summary>
    /// <remarks>
    /// KEFALOS_TEST_3 on 2026-09-02 answered every <c>$value</c> read with
    /// <c>404 Fail to get the LINUX mount point for AttachmentsFolderPath</c> — its Service Layer has
    /// no mount for the attachments folder, so it can serve no file for any document. Where that is
    /// true, this must be <c>Share</c>. The default stays <c>ServiceLayer</c> because it needs no
    /// share credentials and is right once SAP is configured; see docs/sap-credit-note-approvals.md.
    /// </remarks>
    public string AttachmentReadMode { get; set; } = AttachmentReadModes.ServiceLayer;

    public bool ReadsAttachmentsFromShare
        => string.Equals(AttachmentReadMode, AttachmentReadModes.Share, StringComparison.OrdinalIgnoreCase);

    public static class AttachmentReadModes
    {
        public const string ServiceLayer = "ServiceLayer";
        public const string Share = "Share";
    }
}
