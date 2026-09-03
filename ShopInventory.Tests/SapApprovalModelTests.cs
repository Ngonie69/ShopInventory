using System.Text.Json;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The approval-procedure models read what the Service Layer sends. The samples are shaped on the
/// committed <c>$metadata</c> and the Service Layer's JSON conventions (enum member names as strings,
/// <c>value</c> arrays); the Phase 0 spike against KEFALOS_TEST_3 should replace them with real bodies.
/// </summary>
public sealed class SapApprovalModelTests
{
    private const string ApprovalRequestJson = """
        {
          "odata.metadata": "https://sap/b1s/v1/$metadata#ApprovalRequests/@Element",
          "Code": 3110,
          "ApprovalTemplatesID": 7,
          "ObjectType": "14",
          "IsDraft": "tYES",
          "ObjectEntry": null,
          "Status": "arsPending",
          "Remarks": "Returned stock",
          "CurrentStage": 4,
          "OriginatorID": 12,
          "CreationDate": "2026-09-01T00:00:00Z",
          "CreationTime": "09:41:00",
          "DraftEntry": 88123,
          "DraftType": "14",
          "ApprovalRequestLines": [
            { "StageCode": 4, "UserID": 1, "Status": "ardPending", "Remarks": null, "UpdateDate": null, "UpdateTime": null, "CreationDate": "2026-09-01T00:00:00Z", "CreationTime": "09:41:00" },
            { "StageCode": 4, "UserID": 9, "Status": "ardApproved", "Remarks": "ok", "UpdateDate": "2026-09-01T00:00:00Z", "UpdateTime": "10:02:00", "CreationDate": "2026-09-01T00:00:00Z", "CreationTime": "09:41:00" }
          ]
        }
        """;

    private const string DraftJson = """
        {
          "DocEntry": 88123,
          "DocNum": 88123,
          "DocDate": "2026-09-01T00:00:00Z",
          "CardCode": "SPA059",
          "CardName": "Spar Avondale",
          "DocTotal": 245.60,
          "DocCurrency": "USD",
          "Comments": "Damaged goods",
          "DocumentStatus": "bost_Open",
          "Cancelled": "tNO",
          "AttachmentEntry": 5021,
          "AuthorizationStatus": "dasPending",
          "DocObjectCode": "oCreditNotes",
          "DocumentLines": [
            { "LineNum": 0, "ItemCode": "CHE011", "ItemDescription": "Cheddar 1kg", "Quantity": 4.0, "UnitPrice": 61.40, "LineTotal": 245.60, "WarehouseCode": "KEFGRC", "BaseType": 13, "BaseEntry": 77001, "BaseLine": 2 }
          ]
        }
        """;

    private const string AttachmentJson = """
        {
          "AbsoluteEntry": 5021,
          "Attachments2_Lines": [
            { "AbsoluteEntry": 5021, "LineNum": 1, "SourcePath": "\\\\kfldb\\b1_shf\\Paths\\Attachments", "FileName": "return-note-88123", "FileExtension": "pdf", "AttachmentDate": "2026-09-01T00:00:00Z", "FreeText": null },
            { "AbsoluteEntry": 5021, "LineNum": 2, "SourcePath": "\\\\kfldb\\b1_shf\\Paths\\Attachments", "FileName": "photo", "FileExtension": "jpg", "AttachmentDate": "2026-09-01T00:00:00Z", "FreeText": "front of pack" }
          ]
        }
        """;

    private const string StageJson = """
        { "Code": 4, "Name": "Finance review", "NoOfApproversRequired": 1, "ApprovalStageApprovers": [ { "UserID": 1 }, { "UserID": 9 } ] }
        """;

    private const string UserJson = """
        { "InternalKey": 1, "UserCode": "manager", "UserName": "Site Manager", "Superuser": "tYES" }
        """;

    [Fact]
    public void An_approval_request_reads_its_draft_stage_and_approver_lines()
    {
        var request = JsonSerializer.Deserialize<SAPApprovalRequest>(ApprovalRequestJson)!;

        Assert.Equal(3110, request.Code);
        Assert.Equal(SapObjectTypes.CreditNote, request.ObjectType);
        Assert.Equal(SapApprovalRequestStatuses.Pending, request.Status);
        Assert.Equal(88123, request.DraftEntry);
        Assert.Null(request.ObjectEntry);
        Assert.Equal(4, request.CurrentStage);
        Assert.Equal(12, request.OriginatorID);

        var lines = request.ApprovalRequestLines!;
        Assert.Equal(2, lines.Count);
        Assert.Equal(SapApprovalDecisions.Pending, lines[0].Status);
        Assert.Equal(1, lines[0].UserID);
        Assert.Equal(SapApprovalDecisions.Approved, lines[1].Status);
        Assert.Equal("ok", lines[1].Remarks);
    }

    [Fact]
    public void A_draft_carries_its_attachment_and_authorisation_state_beside_the_credit_note_fields()
    {
        var draft = JsonSerializer.Deserialize<SAPCreditNote>(DraftJson)!;

        Assert.Equal(5021, draft.AttachmentEntry);
        Assert.Equal(SapDocumentAuthorizationStatuses.Pending, draft.AuthorizationStatus);
        Assert.Equal(SapDocObjectCodes.CreditNotes, draft.DocObjectCode);
        Assert.Equal(SapDocumentStatuses.Open, draft.DocumentStatus);
        Assert.Equal(245.60m, draft.DocTotal);

        var line = Assert.Single(draft.DocumentLines!);
        Assert.Equal(13, line.BaseType);
        Assert.Equal(77001, line.BaseEntry);
    }

    [Fact]
    public void An_attachment_lists_its_files_with_the_full_name_sap_stores_them_under()
    {
        var attachment = JsonSerializer.Deserialize<SAPAttachment>(AttachmentJson)!;

        Assert.Equal(5021, attachment.AbsoluteEntry);
        var lines = attachment.Attachments2_Lines!;
        Assert.Equal(2, lines.Count);
        Assert.Equal("return-note-88123.pdf", lines[0].FullFileName);
        Assert.Equal("photo.jpg", lines[1].FullFileName);
        Assert.Equal(@"\\kfldb\b1_shf\Paths\Attachments", lines[0].SourcePath);
    }

    [Fact]
    public void A_stage_names_its_approvers_and_a_user_its_code()
    {
        var stage = JsonSerializer.Deserialize<SAPApprovalStage>(StageJson)!;
        var user = JsonSerializer.Deserialize<SAPUser>(UserJson)!;

        Assert.Equal("Finance review", stage.Name);
        Assert.Equal(new[] { 1, 9 }, stage.ApprovalStageApprovers!.Select(approver => approver.UserID!.Value).ToArray());
        Assert.Equal("manager", user.UserCode);
        Assert.Equal(1, user.InternalKey);
    }

    [Theory]
    [InlineData("arsPending", "Pending")]
    [InlineData("arsGeneratedByAuthorizer", "GeneratedByAuthorizer")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Approved", "Approved")]
    public void Statuses_display_as_their_enum_name_without_the_prefix(string? sapValue, string expected)
    {
        Assert.Equal(expected, SapApprovalRequestStatuses.ToDisplay(sapValue));
    }

    [Fact]
    public void Generated_covers_both_ways_a_request_ends_in_a_document()
    {
        Assert.True(SapApprovalRequestStatuses.IsGenerated(SapApprovalRequestStatuses.Generated));
        Assert.True(SapApprovalRequestStatuses.IsGenerated(SapApprovalRequestStatuses.GeneratedByAuthorizer));
        Assert.False(SapApprovalRequestStatuses.IsGenerated(SapApprovalRequestStatuses.Approved));
        Assert.False(SapApprovalRequestStatuses.IsGenerated(null));
    }
}
