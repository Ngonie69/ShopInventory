using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// A SAP Business One attachment record (OATC) as the Service Layer's <c>Attachments2</c> entity set
/// returns it. A document points at one of these through its <c>AttachmentEntry</c>; each line is one
/// file, and the bytes stream from <c>Attachments2(AbsoluteEntry)/$value?filename='name.ext'</c>.
/// </summary>
public class SAPAttachment
{
    [JsonPropertyName("AbsoluteEntry")]
    public int AbsoluteEntry { get; set; }

    [JsonPropertyName("Attachments2_Lines")]
    public List<SAPAttachmentLine>? Attachments2_Lines { get; set; }
}

/// <summary>One attached file (ATC1).</summary>
public class SAPAttachmentLine
{
    [JsonPropertyName("AbsoluteEntry")]
    public int? AbsoluteEntry { get; set; }

    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    /// <summary>The folder the file was attached from — a Windows share path when attached in the B1 client.</summary>
    [JsonPropertyName("SourcePath")]
    public string? SourcePath { get; set; }

    /// <summary>The file name without its extension.</summary>
    [JsonPropertyName("FileName")]
    public string? FileName { get; set; }

    /// <summary>The extension without the dot.</summary>
    [JsonPropertyName("FileExtension")]
    public string? FileExtension { get; set; }

    [JsonPropertyName("AttachmentDate")]
    public string? AttachmentDate { get; set; }

    [JsonPropertyName("FreeText")]
    public string? FreeText { get; set; }

    /// <summary>The full file name SAP stores the file under, <c>name.ext</c>.</summary>
    public string FullFileName =>
        string.IsNullOrWhiteSpace(FileExtension) ? FileName ?? string.Empty : $"{FileName}.{FileExtension}";
}
