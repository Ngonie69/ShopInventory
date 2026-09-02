using Microsoft.AspNetCore.StaticFiles;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>What the page needs to know about a file attached to a draft, before it asks for the bytes.</summary>
internal static class CreditNoteDraftAttachments
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // What the Web page can show inline: a blob <iframe> for a PDF, an <img> for these image types.
    private static readonly HashSet<string> ViewableContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    public static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetContentType(fileName, out var contentType) ? contentType : "application/octet-stream";

    public static bool IsViewable(string contentType) => ViewableContentTypes.Contains(contentType);

    public static string DownloadUrl(int code, int lineNum) =>
        $"/api/credit-note-approvals/{code}/attachments/{lineNum}/download";

    public static CreditNoteDraftAttachmentDto ToDto(int code, SAPAttachmentLine line)
    {
        var fileName = line.FullFileName;
        var contentType = ContentTypeFor(fileName);

        return new CreditNoteDraftAttachmentDto
        {
            LineNum = line.LineNum,
            FileName = fileName,
            Extension = line.FileExtension,
            AttachedOn = CreditNoteApprovalProjection.ParseSapDate(line.AttachmentDate),
            FreeText = line.FreeText,
            ContentType = contentType,
            IsViewable = IsViewable(contentType),
            DownloadUrl = DownloadUrl(code, line.LineNum)
        };
    }
}
