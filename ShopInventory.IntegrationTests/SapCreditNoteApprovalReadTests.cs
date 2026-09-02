using ShopInventory.Models;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer to accept the approval-procedure reads behind the credit note approvals
/// page: the request list and its count, one request with its lines, the draft behind it, the stage,
/// template and user it names, and the bytes of an attached file.
/// </summary>
/// <remarks>
/// Read-only. The two writes — recording a decision and saving the draft to a document — change the
/// company permanently and are proven by hand against the test company, never here.
///
/// The <c>$select</c> lists pass the metadata check in <c>SapSelectClauseTests</c>, but SAP rejects
/// fields per entity set at runtime (it refused <c>BaseEntry</c> on <c>CreditNotes</c> while the shared
/// <c>Document</c> type declares it), and <c>Drafts</c> is exactly such a set. This is where that is
/// settled.
/// </remarks>
[Collection("SAP")]
public class SapCreditNoteApprovalReadTests(SapClientFixture fixture)
{
    private static readonly string[] OpenStatuses =
    [
        SapApprovalRequestStatuses.Pending,
        SapApprovalRequestStatuses.Approved
    ];

    private static readonly string[] AllStatuses =
    [
        SapApprovalRequestStatuses.Pending,
        SapApprovalRequestStatuses.Approved,
        SapApprovalRequestStatuses.NotApproved,
        SapApprovalRequestStatuses.Generated,
        SapApprovalRequestStatuses.GeneratedByAuthorizer,
        SapApprovalRequestStatuses.Cancelled
    ];

    [SapFact]
    public async Task Credit_note_approval_request_list_and_count_are_accepted()
    {
        var (items, total) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(OpenStatuses, 1, 5);

        Assert.True(total >= items.Count, $"SAP counted {total} requests but returned {items.Count} on the first page.");
        Assert.All(items, item => Assert.Equal(SapObjectTypes.CreditNote, item.ObjectType));
    }

    [SapFact]
    public async Task A_request_reads_back_with_its_draft_stage_template_originator_and_attachment()
    {
        var (items, _) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(AllStatuses, 1, 1);
        var first = items.FirstOrDefault();
        Assert.False(
            first is null,
            "SAP holds no credit memo approval requests at all, so the detail reads cannot be exercised. Point these tests at a company where a credit memo has been through the approval procedure.");

        var request = await fixture.Client.GetApprovalRequestAsync(first!.Code);
        Assert.NotNull(request);
        Assert.Equal(first.Code, request.Code);
        Assert.NotNull(request.ApprovalRequestLines);

        if (request.CurrentStage is int stageCode)
        {
            var stage = await fixture.Client.GetApprovalStageAsync(stageCode);
            Assert.NotNull(stage);
            Assert.NotNull(stage.ApprovalStageApprovers);
        }

        if (request.ApprovalTemplatesID is int templateCode)
        {
            Assert.NotNull(await fixture.Client.GetApprovalTemplateAsync(templateCode));
        }

        if (request.OriginatorID is int originator)
        {
            var user = await fixture.Client.GetSapUserAsync(originator);
            Assert.NotNull(user);
            Assert.False(string.IsNullOrWhiteSpace(user.UserCode));
        }

        if (request.DraftEntry is not int draftEntry)
        {
            return;
        }

        // The Drafts set is the one that may reject a field the metadata allows.
        var draft = await fixture.Client.GetCreditNoteDraftAsync(draftEntry);
        var headers = await fixture.Client.GetCreditNoteDraftsAsync([draftEntry]);

        if (draft is null)
        {
            // A generated or cancelled request may have lost its draft; that is SAP's data, not a fault here.
            Assert.Empty(headers);
            return;
        }

        Assert.Equal(SapDocObjectCodes.CreditNotes, draft.DocObjectCode);
        Assert.Single(headers);

        if (draft.AttachmentEntry is not int attachmentEntry)
        {
            return;
        }

        var attachment = await fixture.Client.GetAttachmentAsync(attachmentEntry);
        Assert.NotNull(attachment);
        var line = attachment.Attachments2_Lines?.FirstOrDefault();
        if (line is null)
        {
            return;
        }

        using var download = await fixture.Client.DownloadAttachmentAsync(attachmentEntry, line.FullFileName);
        Assert.NotNull(download);

        var head = new byte[16];
        var read = await download.Content.ReadAsync(head);
        Assert.True(read > 0, $"SAP answered the $value read for '{line.FullFileName}' with an empty body.");
    }

    [SapFact]
    public async Task The_configured_sap_account_resolves_to_a_user()
    {
        // The service approver defaults to the session user, and the stage check needs its InternalKey.
        var user = await fixture.Client.GetSapUserByCodeAsync("manager");

        Assert.NotNull(user);
        Assert.True(user.InternalKey > 0);
    }
}
