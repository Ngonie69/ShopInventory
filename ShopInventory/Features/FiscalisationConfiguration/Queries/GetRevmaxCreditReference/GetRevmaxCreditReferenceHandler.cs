using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxCreditReference;

/// <summary>
/// Reads an invoice's receipt off REVMax and proves which fiscal day it went into.
/// </summary>
/// <remarks>
/// Read-only: one <c>GetInvoice</c> for the invoice, and one per neighbouring receipt the day proof asks
/// about. The day comes only from <see cref="IDesktopCreditFiscalDays"/>, the proof the till's own credit
/// notes use — a receipt of ours the device confirms shares this one's <c>global - counter</c>.
/// </remarks>
public sealed class GetRevmaxCreditReferenceHandler(
    IRevmaxClient client,
    IDesktopCreditFiscalDays fiscalDays,
    IOptions<RevmaxSettings> settings,
    ILogger<GetRevmaxCreditReferenceHandler> logger
) : IRequestHandler<GetRevmaxCreditReferenceQuery, ErrorOr<RevmaxCreditReferenceResult>>
{
    public async Task<ErrorOr<RevmaxCreditReferenceResult>> Handle(
        GetRevmaxCreditReferenceQuery query,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.RevmaxCredit.RevmaxDisabled;

        var docNum = query.InvoiceDocNum;
        var number = docNum.ToString(CultureInfo.InvariantCulture);
        InvoiceResponse? response;

        try
        {
            response = await client.GetInvoiceAsync(number, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "REVMax lookup for invoice {DocNum} failed", docNum);
            return Errors.RevmaxCredit.DeviceUnavailable(docNum, ex.Message);
        }

        if (response is null)
            return Errors.RevmaxCredit.DeviceUnavailable(docNum, "the device returned no answer");

        if (!response.Success)
        {
            // The device's busy state comes back in the same shape as "Invoice not Found" (Code "0", no
            // data). Only the second is an answer — see RevmaxFiscalReceiptReader.
            return response.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true
                ? Errors.RevmaxCredit.NotFiscalised(docNum)
                : Errors.RevmaxCredit.DeviceUnavailable(docNum, $"Code {response.Code}: {response.Message}");
        }

        // GetInvoice is not scoped to our device: the box serves several and answers a number with whichever
        // device's receipt carries it, so DeviceID is the only thing that says the receipt is ours.
        var device = settings.Value.DefaultRefDeviceId;
        var deviceText = device.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(response.DeviceID?.Trim(), deviceText, StringComparison.Ordinal))
            return Errors.RevmaxCredit.NotOurReceipt(docNum, $"it belongs to device {response.DeviceID ?? "(none)"}, not {device}");

        if (response.Data is not { } data)
            return Errors.RevmaxCredit.Incomplete(docNum, "the receipt body");

        if (!string.Equals(data.ReceiptType, "FiscalInvoice", StringComparison.OrdinalIgnoreCase))
            return Errors.RevmaxCredit.NotOurReceipt(docNum, $"it is a {data.ReceiptType ?? "(no type)"}, not a fiscal invoice");

        if (!(data.InvoiceNo == number || data.InvoiceNo == $"{deviceText}-{number}"))
            return Errors.RevmaxCredit.NotOurReceipt(docNum, $"it was filed as invoice '{data.InvoiceNo}'");

        var missing = new List<string>();
        if (data.ReceiptGlobalNo is <= 0 or > int.MaxValue) missing.Add("receipt global number");
        if (data.ReceiptCounter <= 0) missing.Add("receipt counter");
        if (string.IsNullOrWhiteSpace(data.ReceiptCurrency)) missing.Add("currency");
        if (data.ReceiptTotal <= 0) missing.Add("total");
        if (data.ReceiptTaxes is not { Count: > 0 }) missing.Add("taxes");
        if (!DateTime.TryParse(data.ReceiptDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var receiptDate))
            missing.Add("receipt date");
        if (missing.Count > 0)
            return Errors.RevmaxCredit.Incomplete(docNum, string.Join(", ", missing));

        var global = (int)data.ReceiptGlobalNo;

        // Neighbours are found by when they were filed. The device's clock is local; our sales are stored
        // in UTC. The search window is days wide, so the conversion only has to be the right way round.
        var receiptUtc = receiptDate.Kind == DateTimeKind.Utc ? receiptDate : receiptDate.ToUniversalTime();
        var day = await fiscalDays.ResolveAsync(receiptUtc, global, data.ReceiptCounter, cancellationToken);

        string? unresolved = null;
        if (day is null)
        {
            unresolved =
                $"No receipt of ours filed within days of {receiptDate:yyyy-MM-dd} is confirmed by REVMax to share " +
                $"receipt {global}'s fiscal day. Read the day off the ZIMRA taxpayer portal for device {device}.";
            logger.LogInformation(
                "Invoice {DocNum}'s REVMax receipt {Receipt} (counter {Counter}) could not be placed on a fiscal day",
                docNum, global, data.ReceiptCounter);
        }

        return new RevmaxCreditReferenceResult(
            docNum,
            device,
            global,
            data.ReceiptCounter,
            day,
            unresolved,
            DateTime.SpecifyKind(receiptDate, DateTimeKind.Unspecified),
            data.ReceiptCurrency!.Trim().ToUpperInvariant(),
            data.ReceiptTotal,
            data.ReceiptTaxes!
                .Select(tax => new RevmaxCreditReferenceTax(
                    tax.TaxID,
                    tax.TaxPercent,
                    string.IsNullOrWhiteSpace(tax.TaxCode) ? null : tax.TaxCode.Trim(),
                    tax.SalesAmountWithTax,
                    tax.TaxAmount))
                .ToList(),
            string.IsNullOrWhiteSpace(response.VerificationCode) ? null : response.VerificationCode,
            string.IsNullOrWhiteSpace(response.QRcode) ? null : response.QRcode);
    }
}
