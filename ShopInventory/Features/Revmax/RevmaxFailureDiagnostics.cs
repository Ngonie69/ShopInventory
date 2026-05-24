using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax;

internal static class RevmaxFailureDiagnostics
{
    private const string FailureSource = "REVMaxUpstream";

    internal static string BuildHandledFailureMessage(
        string? invoiceNumber,
        string? responseCode,
        string? responseMessage)
    {
        var normalizedInvoice = string.IsNullOrWhiteSpace(invoiceNumber)
            ? "unknown"
            : invoiceNumber.Trim();
        var normalizedCode = string.IsNullOrWhiteSpace(responseCode)
            ? "unknown"
            : responseCode.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(responseMessage)
            ? "No message returned from REVMax."
            : responseMessage.Trim();

        return $"REVMax upstream failure for invoice {normalizedInvoice} (Code={normalizedCode}): {normalizedMessage}";
    }

    internal static object BuildHandledFailurePayload(
        string endpointName,
        string? invoiceNumber,
        TransactMResponse response,
        string? originalMessage,
        string normalizedMessage)
    {
        return new
        {
            FailureSource,
            Endpoint = endpointName,
            InvoiceNumber = invoiceNumber,
            ResponseCode = response.Code,
            ResponseMessage = originalMessage,
            NormalizedMessage = normalizedMessage,
            UpstreamResponse = new
            {
                response.Code,
                Message = originalMessage,
                response.QRcode,
                response.VerificationCode,
                response.VerificationLink,
                response.DeviceID,
                response.DeviceSerialNumber,
                response.FiscalDay,
                response.FiscalDayNo,
                response.ReceiptGlobalNo,
                response.ReceiptCounter,
                response.DeviceSerial,
                response.FiscalDayDate,
                response.ReceiptDate,
                response.Data
            }
        };
    }
}