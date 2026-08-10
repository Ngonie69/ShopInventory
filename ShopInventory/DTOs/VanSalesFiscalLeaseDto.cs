using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// A van handset's fiscal lease: the identity it signs receipts as, the day it signs them into, the
/// sequence position to start from, and the tax it must apply.
///
/// It is collected while online and spent while offline, which is what makes it a lease rather than a
/// status response. The sequence in particular is borrowed on the understanding that this handset is the
/// only writer of its device's receipt chain — the server cannot enforce that, so it is a registration
/// rule: one ZIMRA device per handset, never shared.
/// </summary>
public sealed class VanSalesFiscalLeaseDto
{
    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("device_serial_no")]
    public string DeviceSerialNo { get; set; } = string.Empty;

    /// <summary>Per-device, and the first segment of every QR payload the handset prints.</summary>
    [JsonPropertyName("qr_url")]
    public string QrUrl { get; set; } = string.Empty;

    [JsonPropertyName("fiscal_day_no")]
    public int FiscalDayNo { get; set; }

    /// <summary>
    /// Local wall clock with no offset, deliberately. The handset compares it against its own clock to
    /// decide whether the day has outrun the taxpayer's limit, and an offset would make that comparison
    /// depend on where this server happens to run.
    /// </summary>
    [JsonPropertyName("fiscal_day_opened_at")]
    public string? FiscalDayOpenedAt { get; set; }

    /// <summary>False unless FDMS reports the day open. The handset refuses to trade offline without it.</summary>
    [JsonPropertyName("fiscal_day_open")]
    public bool FiscalDayOpen { get; set; }

    [JsonPropertyName("taxpayer_day_max_hrs")]
    public int TaxPayerDayMaxHrs { get; set; }

    [JsonPropertyName("certificate_valid_till")]
    public string? CertificateValidTill { get; set; }

    /// <summary>The global number the handset's next receipt takes. Never resets.</summary>
    [JsonPropertyName("next_global_no")]
    public int NextGlobalNo { get; set; }

    /// <summary>The counter the handset's next receipt takes. Restarts at 1 each fiscal day.</summary>
    [JsonPropertyName("next_counter")]
    public int NextCounter { get; set; }

    [JsonPropertyName("taxes")]
    public List<VanSalesFiscalTaxDto> Taxes { get; set; } = [];

    /// <summary>
    /// What tax each item attracts, and its HS code. Items whose VAT group SAP leaves blank, or whose
    /// group this taxpayer has no FDMS tax id for, are absent — the handset then refuses to sell them
    /// offline by name, which is the outcome we want over a plausible default.
    /// </summary>
    [JsonPropertyName("item_taxes")]
    public List<VanSalesFiscalItemTaxDto> ItemTaxes { get; set; } = [];
}

public sealed class VanSalesFiscalTaxDto
{
    [JsonPropertyName("tax_id")]
    public int TaxId { get; set; }

    /// <summary>Null is untaxed, 0 is zero-rated; they are signed differently and must stay distinct.</summary>
    [JsonPropertyName("percent")]
    public decimal? Percent { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public sealed class VanSalesFiscalItemTaxDto
{
    [JsonPropertyName("item_code")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("tax_id")]
    public int TaxId { get; set; }

    [JsonPropertyName("hs_code")]
    public string? HsCode { get; set; }
}
