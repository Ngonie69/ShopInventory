using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.CreditNoteApprovals.Queries.DownloadCreditNoteDraftAttachment;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Reading a file attached to a held draft. The route is keyed on the approval request, so the only
/// files reachable are those of a draft in the approval queue — and only by the line number SAP holds
/// them under.
/// </summary>
public sealed class CreditNoteDraftAttachmentDownloadTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 return note");

    [Fact]
    public async Task The_bytes_come_from_the_service_layer_by_default()
    {
        var sap = new RecordingSapClient();
        var handler = Handler(sap, readFromShare: false);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 1), CancellationToken.None);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        using var download = result.Value;
        Assert.Equal("return-note.pdf", download.FileName);
        Assert.Equal("application/pdf", download.ContentType);
        using var read = new MemoryStream();
        await download.Content.CopyToAsync(read);
        Assert.Equal(Pdf, read.ToArray());

        Assert.Equal((5021, "return-note.pdf"), sap.StreamedFrom);
        Assert.Null(sap.ReadFromShare);
    }

    [Fact]
    public async Task Share_mode_reads_the_file_off_the_attachments_folder_instead()
    {
        var sap = new RecordingSapClient();
        var handler = Handler(sap, readFromShare: true);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 2), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("photo.jpg", result.Value.FileName);
        Assert.Equal("photo.jpg", sap.ReadFromShare?.FullFileName);
        Assert.Null(sap.StreamedFrom);
    }

    [Fact]
    public async Task A_line_that_is_not_on_the_draft_is_refused_before_any_bytes_are_asked_for()
    {
        var sap = new RecordingSapClient();
        var handler = Handler(sap, readFromShare: false);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 9), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("CreditNoteApproval.AttachmentNotFound", result.FirstError.Code);
        Assert.Null(sap.StreamedFrom);
    }

    [Fact]
    public async Task A_request_for_any_other_document_type_is_not_found()
    {
        var sap = new RecordingSapClient { RequestObjectType = "13" };
        var handler = Handler(sap, readFromShare: false);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 1), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.NotFound", result.FirstError.Code);
        Assert.Null(sap.StreamedFrom);
    }

    [Fact]
    public async Task Saps_refusal_is_reported_in_its_own_words()
    {
        var sap = new RecordingSapClient { RefuseStream = true };
        var handler = Handler(sap, readFromShare: false);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 1), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.AttachmentUnavailable", result.FirstError.Code);
        Assert.Contains("Attachment folder is not accessible", result.FirstError.Description);
    }

    /// <summary>
    /// A file the drawer is listing by name must never come back as "there is no such attachment".
    /// The line is on the document; only the bytes are missing, and the two need different words or
    /// somebody goes looking for a document that is right there.
    /// </summary>
    [Fact]
    public async Task A_line_that_exists_but_yields_no_bytes_is_unavailable_not_missing()
    {
        var sap = new RecordingSapClient { StreamReturnsNothing = true };
        var handler = Handler(sap, readFromShare: false);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 1), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.AttachmentUnavailable", result.FirstError.Code);
        Assert.Contains("return-note.pdf", result.FirstError.Description);
        Assert.DoesNotContain("no attachment line", result.FirstError.Description);
    }

    [Fact]
    public async Task Share_mode_says_the_file_is_not_in_the_folder_when_it_is_absent()
    {
        var sap = new RecordingSapClient { ShareReturnsNothing = true };
        var handler = Handler(sap, readFromShare: true);

        var result = await handler.Handle(new DownloadCreditNoteDraftAttachmentQuery(3110, 2), CancellationToken.None);

        Assert.Equal("CreditNoteApproval.AttachmentUnavailable", result.FirstError.Code);
        Assert.Contains("photo.jpg", result.FirstError.Description);
        Assert.Contains("attachments folder", result.FirstError.Description);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static DownloadCreditNoteDraftAttachmentHandler Handler(RecordingSapClient sap, bool readFromShare) => new(
        sap.AsClient(),
        Options.Create(new SAPSettings { Enabled = true }),
        Options.Create(new CreditNoteApprovalSettings
        {
            AttachmentReadMode = readFromShare
                ? CreditNoteApprovalSettings.AttachmentReadModes.Share
                : CreditNoteApprovalSettings.AttachmentReadModes.ServiceLayer
        }),
        NullLogger<DownloadCreditNoteDraftAttachmentHandler>.Instance);

    private sealed class RecordingSapClient
    {
        public string RequestObjectType { get; init; } = SapObjectTypes.CreditNote;
        public bool RefuseStream { get; init; }
        public bool StreamReturnsNothing { get; init; }
        public bool ShareReturnsNothing { get; init; }
        public (int AbsoluteEntry, string FileName)? StreamedFrom { get; private set; }
        public SAPAttachmentLine? ReadFromShare { get; private set; }

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetApprovalRequestAsync) => Task.FromResult<SAPApprovalRequest?>(new SAPApprovalRequest
            {
                Code = (int)args![0]!, ObjectType = RequestObjectType, Status = SapApprovalRequestStatuses.Pending, DraftEntry = 88123
            }),
            nameof(ISAPServiceLayerClient.GetCreditNoteDraftsAsync) => Task.FromResult(new List<SAPCreditNote>
            {
                new() { DocEntry = 88123, DocObjectCode = SapDocObjectCodes.CreditNotes, AttachmentEntry = 5021 }
            }),
            nameof(ISAPServiceLayerClient.GetAttachmentAsync) => Task.FromResult<SAPAttachment?>(new SAPAttachment
            {
                AbsoluteEntry = 5021,
                Attachments2_Lines =
                [
                    new SAPAttachmentLine { LineNum = 1, FileName = "return-note", FileExtension = "pdf" },
                    new SAPAttachmentLine { LineNum = 2, FileName = "photo", FileExtension = "jpg" }
                ]
            }),
            nameof(ISAPServiceLayerClient.DownloadAttachmentAsync) => Stream((int)args![0]!, (string)args[1]!),
            nameof(ISAPServiceLayerClient.ReadAttachmentFromShareAsync) => Share((SAPAttachmentLine)args![0]!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private Task<SapAttachmentDownload?> Stream(int absoluteEntry, string fileName)
        {
            if (RefuseStream)
            {
                throw new SapRequestRejectedException("download attachment", HttpStatusCode.BadRequest, "Attachment folder is not accessible");
            }

            StreamedFrom = (absoluteEntry, fileName);
            return Task.FromResult(StreamReturnsNothing
                ? null
                : new SapAttachmentDownload(new MemoryStream(Pdf), "application/pdf", fileName));
        }

        private Task<SapAttachmentDownload?> Share(SAPAttachmentLine line)
        {
            ReadFromShare = line;
            return Task.FromResult(ShareReturnsNothing
                ? null
                : new SapAttachmentDownload(new MemoryStream([1, 2, 3]), "image/jpeg", line.FullFileName));
        }
    }
}
