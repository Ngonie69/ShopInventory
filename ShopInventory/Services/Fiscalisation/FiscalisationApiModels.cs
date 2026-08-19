using System.Text.Json.Serialization;

namespace ShopInventory.Services.Fiscalisation;

// Wire contract for the Fiscalisation platform's /api surface. Copied field-for-field from the
// platform's own FiscalIntegrationEndpoints.ApiModels.cs and Sap/SapServiceLayerDtos.cs, the same way
// its SAP bridge carries a copy. There is no shared contracts package, so nothing checks that the two
// stay in step — that is a human check when the platform's API changes.
//
// The platform registers no JsonStringEnumConverter on its HTTP surface, so these enums travel as
// integers in request and response bodies. The numeric values below are therefore load-bearing.

public enum ReceiptType
{
    FiscalInvoice = 0,
    CreditNote = 1,
    DebitNote = 2
}

public enum MoneyType
{
    Cash = 0,
    Card = 1,
    MobileWallet = 2,
    Coupon = 3,
    Credit = 4,
    BankTransfer = 5,
    Other = 6
}

public enum ReceiptPrintForm
{
    Receipt48 = 0,
    InvoiceA4 = 1
}

public enum SapDocumentType
{
    Invoice = 0,
    CreditNote = 1,
    DebitNote = 2
}

// Not sealed, matching the platform: IngestSignedReceiptApiRequest extends it so the pre-signed path
// describes the receipt in exactly the same terms the ordinary submit path does. That shared shape is
// load-bearing — the platform rebuilds the receipt from these fields with one method for both paths, and
// a signature only verifies if both sides derive the payload identically.
public class SubmitReceiptApiRequest
{
    public int DeviceId { get; set; }
    public string? InvoiceNo { get; set; }
    public ReceiptType ReceiptType { get; set; } = ReceiptType.FiscalInvoice;
    public string? Currency { get; set; } = "USD";
    public DateTime? ReceiptDate { get; set; }
    public bool TaxInclusive { get; set; } = true;
    public MoneyType? PaymentType { get; set; }
    public decimal PaymentAmount { get; set; }
    public List<LineApiRequest> Lines { get; set; } = [];
    public BuyerApiRequest? Buyer { get; set; }
    public CreditDebitNoteApiRequest? CreditDebitNote { get; set; }
    public string? ReceiptNotes { get; set; }
    public ReceiptPrintForm? ReceiptPrintForm { get; set; } = Fiscalisation.ReceiptPrintForm.InvoiceA4;
    public string? Username { get; set; }
    public string? UserNameSurname { get; set; }
}

/// <summary>
/// A receipt a client signed with the device's own key, for archiving as-is.
///
/// The authority is inverted from <see cref="SubmitReceiptApiRequest"/>: there the platform assigns the
/// sequence and signs, here the client did both while it was out of coverage and the platform verifies and
/// records. Every field below is part of what was signed or of what proves it, so none may be adjusted on
/// the way — the platform re-derives the payload and refuses anything that does not hash to
/// <see cref="DeviceSignatureHash"/>.
/// </summary>
public sealed class IngestSignedReceiptApiRequest : SubmitReceiptApiRequest
{
    /// <summary>The fiscal day the handset opened locally and signed this receipt into.</summary>
    public int FiscalDayNo { get; set; }

    /// <summary>
    /// When that day was opened on the handset. The platform never opened this day itself and has no
    /// other way to learn it, and the offline file's header declares it.
    /// </summary>
    public DateTime FiscalDayOpenedAt { get; set; }

    public int ReceiptCounter { get; set; }

    public int ReceiptGlobalNo { get; set; }

    /// <summary>
    /// The hash this receipt was chained onto, or null for the first receipt of a fiscal day. Sent
    /// explicitly so a divergence is reported as a chain break rather than an unexplained signature
    /// failure.
    /// </summary>
    public string? PreviousReceiptHash { get; set; }

    /// <summary>Base64 SHA-256 of the canonical payload the handset signed.</summary>
    public string? DeviceSignatureHash { get; set; }

    /// <summary>Base64 RSA-PKCS1-SHA256 signature over that same payload.</summary>
    public string? DeviceSignatureValue { get; set; }
}

public sealed class LineApiRequest
{
    public string? Name { get; set; }
    public string? HsCode { get; set; }
    public decimal Quantity { get; set; } = 1;

    /// <summary>
    /// Unit price. The platform rejects a credit note whose line prices are not negative, and an
    /// invoice or debit note whose line prices are not positive.
    /// </summary>
    public decimal Price { get; set; }

    public decimal? TaxPercent { get; set; }
    public int TaxId { get; set; }
    public string? TaxCode { get; set; }
}

public sealed class BuyerApiRequest
{
    public string? RegisterName { get; set; }
    public string? TradeName { get; set; }
    public string? Tin { get; set; }
    public string? VatNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Street { get; set; }
    public string? HouseNo { get; set; }
}

public sealed class CreditDebitNoteApiRequest
{
    public long? ReceiptID { get; set; }
    public int? DeviceID { get; set; }
    public int? FiscalDayNo { get; set; }
    public int? ReceiptGlobalNo { get; set; }
}

public sealed class SubmitReceiptApiResponse
{
    public bool Success { get; set; }
    public int DeviceId { get; set; }
    public int FiscalDayNo { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime ReceiptDate { get; set; }
    public ReceiptType ReceiptType { get; set; } = ReceiptType.FiscalInvoice;
    public decimal ReceiptTotal { get; set; }
    public long ReceiptId { get; set; }
    public DateTime ServerDate { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public int ReceiptCounter { get; set; }
    public int ReceiptGlobalNo { get; set; }

    /// <summary>
    /// Base64 device signature. This is the input to the QR verification segment — the platform does
    /// not return a ready-made QR code. See <see cref="FiscalReceiptQrComposer"/>.
    /// </summary>
    public string? DeviceSignatureValue { get; set; }

    public string? DeviceSignatureHash { get; set; }
    public string? ServerSignatureHash { get; set; }
    public string? ServerSignatureValue { get; set; }
}

public sealed class SapDocumentReference
{
    public SapDocumentType DocumentType { get; set; } = SapDocumentType.Invoice;
    public int? DocEntry { get; set; }
    public int? DocNum { get; set; }
}

public sealed class SapFiscaliseReceiptApiRequest
{
    public SapDocumentReference? SapDocument { get; set; }

    /// <summary>
    /// Overrides only. The platform reads the document's lines, buyer and totals from SAP itself, so
    /// this must not carry lines.
    /// </summary>
    public SubmitReceiptApiRequest? Receipt { get; set; }
}

public sealed class CheckFiscalisedReceiptApiResponse
{
    public bool IsFiscalised { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;
    public List<FiscalisedReceiptRecordDto> Matches { get; set; } = [];
}

public sealed class FiscalisedReceiptRecordDto
{
    public int DeviceId { get; set; }
    public int FiscalDayNo { get; set; }
    public string ReceiptType { get; set; } = string.Empty;
    public string ReceiptCurrency { get; set; } = string.Empty;
    public int ReceiptCounter { get; set; }
    public int ReceiptGlobalNo { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime ReceiptDate { get; set; }
    public decimal ReceiptTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal AmountExcludingTax { get; set; }
    public long ReceiptId { get; set; }
    public DateTime ServerDate { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public DateTime ArchivedAt { get; set; }
    public string? DeviceSignatureHash { get; set; }
    public string? DeviceSignatureValue { get; set; }
    public string? ServerSignatureHash { get; set; }
    public string? ServerSignatureValue { get; set; }
}

/// <summary>
/// The platform's verdict on a receipt it has not been asked to fiscalise.
/// </summary>
/// <remarks>
/// A failed preflight is still HTTP 200 with <see cref="Valid"/> false. That is the point of the
/// endpoint: a 4xx would be indistinguishable from the request itself being wrong, and the whole reason
/// to ask is to find out which.
/// </remarks>
public sealed class PreflightReceiptApiResponse
{
    public int DeviceId { get; set; }

    public bool Valid { get; set; }

    /// <summary>
    /// Every rule the receipt breaks, in the platform's own wording — each names the FDMS code it
    /// prevents, so they can be read alongside local findings without translation.
    /// </summary>
    public List<string> Failures { get; set; } = [];
}

public sealed class FiscalConfigApiResponse
{
    public string OperationID { get; set; } = string.Empty;
    public string TaxPayerName { get; set; } = string.Empty;
    public string TaxPayerTIN { get; set; } = string.Empty;
    public string? VatNumber { get; set; }
    public string DeviceSerialNo { get; set; } = string.Empty;
    public string DeviceBranchName { get; set; } = string.Empty;
    public string DeviceOperatingMode { get; set; } = string.Empty;
    public int TaxPayerDayMaxHrs { get; set; }
    public int TaxpayerDayEndNotificationHrs { get; set; }
    public List<FiscalTaxDto> ApplicableTaxes { get; set; } = [];
    public DateTime CertificateValidTill { get; set; }

    /// <summary>
    /// Base URL for ZIMRA receipt verification. The first segment of every QR payload.
    /// </summary>
    public string QrUrl { get; set; } = string.Empty;
}

public sealed class FiscalTaxDto
{
    public int TaxID { get; set; }
    public decimal? TaxPercent { get; set; }
    public string TaxName { get; set; } = string.Empty;
    public DateTime TaxValidFrom { get; set; }
    public DateTime? TaxValidTill { get; set; }
}

public sealed class FiscalStatusApiResponse
{
    public int DeviceId { get; set; }
    public int FiscalDayNo { get; set; }
    public string FiscalDayStatus { get; set; } = string.Empty;
    public DateTime? FiscalDayOpened { get; set; }
    public DateTime? LastReceiptDate { get; set; }
    public int LastReceiptGlobalNo { get; set; }
    public int LastReceiptCounter { get; set; }
}

public sealed class ErrorApiResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}
