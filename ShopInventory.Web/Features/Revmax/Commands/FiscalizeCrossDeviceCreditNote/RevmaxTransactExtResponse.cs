using System.Text.Json.Serialization;

namespace ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

public sealed class RevmaxTransactExtResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public string? FiscalDayNo { get; set; }
    public string? ReceiptGlobalNo { get; set; }
    public string? ReceiptCounter { get; set; }
    public string? DeviceSerial { get; set; }
    public string? FiscalDayDate { get; set; }
    public string? ReceiptDate { get; set; }
    public object? Data { get; set; }

    [JsonIgnore]
    public bool Success => Code == "1";
}