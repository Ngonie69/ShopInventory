using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The invoices a van's proof-of-delivery screen offers: every invoice in the range for a shop on the
/// drivers' shop list, with whether the office already holds a delivery note for it.
/// </summary>
public class VanSalesPodDeliveriesDto
{
    [JsonPropertyName("fromDate")]
    public string FromDate { get; set; } = string.Empty;

    [JsonPropertyName("toDate")]
    public string ToDate { get; set; } = string.Empty;

    /// <summary>
    /// How many shops are on the drivers' list. Zero is its own answer on the handset — the office has
    /// not chosen any shops yet — and is not the same as a list of shops with nothing invoiced.
    /// </summary>
    [JsonPropertyName("shopCount")]
    public int ShopCount { get; set; }

    /// <summary>False when credit notes could not all be read, so a credited invoice may show as owed a note.</summary>
    [JsonPropertyName("creditNoteDataComplete")]
    public bool CreditNoteDataComplete { get; set; } = true;

    /// <summary>Newest first. Fully credited invoices are included and flagged, never dropped.</summary>
    [JsonPropertyName("invoices")]
    public List<VanSalesPodDeliveryDto> Invoices { get; set; } = new();
}

/// <summary>One invoice on the list, with its delivery note status.</summary>
public class VanSalesPodDeliveryDto
{
    /// <summary>The SAP document entry. What a page is filed against, through <c>pod/invoice/{docEntry}/file</c>.</summary>
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("docDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("cardCode")]
    public string? CardCode { get; set; }

    [JsonPropertyName("cardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("docCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("hasPod")]
    public bool HasPod { get; set; }

    [JsonPropertyName("podCount")]
    public int PodCount { get; set; }

    /// <summary>When the latest page was filed, in UTC.</summary>
    [JsonPropertyName("podUploadedAt")]
    public DateTime? PodUploadedAt { get; set; }

    [JsonPropertyName("isFullyCredited")]
    public bool IsFullyCredited { get; set; }

    [JsonPropertyName("creditNoteNumber")]
    public string? CreditNoteNumber { get; set; }

    [JsonPropertyName("uploaders")]
    public List<VanSalesPodUploaderDto> Uploaders { get; set; } = new();
}

/// <summary>Who filed pages against an invoice, and how many.</summary>
public class VanSalesPodUploaderDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    /// <summary>In UTC.</summary>
    [JsonPropertyName("latestUploadedAt")]
    public DateTime? LatestUploadedAt { get; set; }
}
