using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>Credits the receipt REVMax already holds. No SAP or dormant platform endpoint is called.</summary>
public sealed class RevmaxDesktopCreditGateway(IRevmaxClient client, RevmaxFiscalizationService fiscal,
    IOptions<RevmaxSettings> settings, IOptions<FiscalisationSettings> selection) : IDesktopCreditFiscalGateway
{
    private void RequireEnabled()
    {
        if (selection.Value.Provider != FiscalisationProvider.Revmax || !settings.Value.Enabled)
            throw new InvalidOperationException("REVMax must be enabled for desktop credit notes.");
    }

    public async Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct)
    {
        RequireEnabled();
        var number = fiscal.BuildPreSapInvoiceNo(sale.ExternalReferenceId);
        var original = await client.GetInvoiceAsync(number, ct);
        if (original?.Success != true || original.Data is not { ReceiptLines.Count: > 0 } data ||
            string.IsNullOrWhiteSpace(data.ReceiptCurrency) || data.ReceiptTotal <= 0)
            throw new InvalidOperationException("REVMax could not confirm the original fiscal receipt and its lines.");
        if (!int.TryParse(original.DeviceID, out var device) || device != settings.Value.DefaultRefDeviceId ||
            !string.Equals(data.ReceiptType, "FiscalInvoice", StringComparison.OrdinalIgnoreCase) ||
            !(data.InvoiceNo == number || data.InvoiceNo == $"{device}-{number}"))
            throw new InvalidOperationException("REVMax returned a different invoice or device. The credit cannot be prepared.");
        // GetInvoice.FiscalDay is not the receipt's day. Use the day recorded when this sale was filed,
        // and prove that its sequence is the one REVMax returned before using it as the fiscal link.
        var global = data.ReceiptGlobalNo;
        if (!int.TryParse(sale.FiscalDayNo, out var day) || day <= 0 || global <= 0 || global > int.MaxValue ||
            (!string.IsNullOrWhiteSpace(sale.FiscalReceiptNumber) &&
             (!long.TryParse(sale.FiscalReceiptNumber, out var recordedGlobal) || recordedGlobal != global)))
            throw new InvalidOperationException("The original sale's recorded fiscal day or receipt number is missing or does not match REVMax. Reconcile the original receipt first.");
        // A credit needs a quantity, a value and the tax the line was sold under; receiptLineNo and
        // receiptLineType are the device's own bookkeeping and it does not have to echo either. Absent
        // or repeated numbers only cost the line its identity, which its position gives back, so they
        // are not a reason to refuse the receipt. A line that genuinely cannot be reversed as goods is
        // left off the form with its reason rather than blocking the rest: the credit is still capped
        // at the receipt total, so leaving one out can never credit more than the receipt carried.
        var numbers = data.ReceiptLines.Select(l => l.ReceiptLineNo).ToList();
        var byPosition = numbers.Any(n => n <= 0) || numbers.Distinct().Count() != numbers.Count;
        var lines = new List<DesktopCreditLine>();
        var excluded = new List<string>();
        for (var index = 0; index < data.ReceiptLines.Count; index++)
        {
            var l = data.ReceiptLines[index];
            var lineNo = byPosition ? index + 1 : l.ReceiptLineNo;
            var name = string.IsNullOrWhiteSpace(l.ReceiptLineName) ? $"Line {lineNo}" : l.ReceiptLineName;
            if (Uncreditable(l) is { } reason) { excluded.Add($"line {lineNo} ({name}) {reason}"); continue; }
            lines.Add(new DesktopCreditLine(lineNo, name, l.ReceiptLineQuantity,
                // The recorded line total already incorporates discounts and the device's rounding.
                l.ReceiptLineTotal * (data.ReceiptLinesTaxInclusive ? 1m : 1m + l.TaxPercent / 100m) / l.ReceiptLineQuantity,
                l.TaxID, l.TaxPercent, l.TaxCode, l.ReceiptLineHSCode));
        }
        if (lines.Count == 0)
            throw new InvalidOperationException(
                $"None of the original receipt's {data.ReceiptLines.Count} lines can be credited: {string.Join("; ", excluded)}.");
        return new DesktopCreditSource(number, data.ReceiptCurrency ?? sale.Currency, Math.Abs(data.ReceiptTotal),
            device, day, checked((int)global), null, lines, ExcludedLines: excluded.Count == 0 ? null : excluded,
            Buyer: new BuyerApiRequest
            {
                RegisterName = ReadText(data.BuyerData, "buyerRegisterName") ?? sale.CardName ?? sale.CardCode,
                Tin = ReadText(data.BuyerData, "buyerTIN"), VatNumber = ReadText(data.BuyerData, "buyerVATNumber"),
                Phone = ReadNestedText(data.BuyerData, "buyerContacts", "phoneNo"),
                Email = ReadNestedText(data.BuyerData, "buyerContacts", "email"),
                Street = ReadNestedText(data.BuyerData, "buyerAddress", "street"),
                City = ReadNestedText(data.BuyerData, "buyerAddress", "city")
            });
    }

    /// <summary>Why the device's record of a line cannot be reversed as goods, or null when it can.</summary>
    /// <remarks>
    /// A type the device did state and that is not a sale - FDMS's other line type is Discount - must
    /// never be credited as goods. A type it did not state says nothing, and refusing on it would
    /// refuse every receipt whose lines carry the fiscal content but not that field.
    /// </remarks>
    private static string? Uncreditable(ReceiptLine line) =>
        !string.IsNullOrWhiteSpace(line.ReceiptLineType)
            && !string.Equals(line.ReceiptLineType, "Sale", StringComparison.OrdinalIgnoreCase)
                ? $"is recorded as {line.ReceiptLineType}, not a sale"
        : line.ReceiptLineQuantity <= 0 ? "carries no quantity"
        : line.ReceiptLineTotal <= 0 ? "carries no value"
        : line.TaxID <= 0 ? "has no tax id on the receipt"
        : line.TaxPercent < 0 ? $"has a negative tax rate ({line.TaxPercent}%)"
        : null;

    private static string? ReadText(JsonElement? value, string property) =>
        value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(property, out var item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static string? ReadNestedText(JsonElement? value, string parent, string property) =>
        value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(parent, out var item)
            ? ReadText(item, property) : null;

    public async Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct)
    {
        RequireEnabled();
        var number = plan.Receipt.InvoiceNo!;
        var existing = await client.GetInvoiceAsync(number, ct)
            ?? throw new InvalidOperationException("REVMax returned no answer to the saved credit-note lookup.");
        if (!existing.Success)
        {
            if (existing.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true) return null;
            throw new InvalidOperationException("REVMax could not establish whether the saved credit note exists.");
        }
        var receipt = existing.Data;
        var device = settings.Value.DefaultRefDeviceId.ToString(CultureInfo.InvariantCulture);
        if (receipt is null || existing.DeviceID != device ||
            !string.Equals(receipt.ReceiptType, "CreditNote", StringComparison.OrdinalIgnoreCase) ||
            !(receipt.InvoiceNo == number || receipt.InvoiceNo == $"{device}-{number}") ||
            Math.Abs(receipt.ReceiptTotal) != plan.Amount || receipt.ReceiptCurrency != plan.Source.Currency)
            throw new InvalidOperationException("The saved credit-note lookup returned a different fiscal document.");
        return new FiscalizationResult
        {
            Success = true, InvoiceNumber = number, QRCode = existing.QRcode,
            VerificationCode = existing.VerificationCode, ReceiptGlobalNo = receipt.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
            Message = "The credit note's existing REVMax receipt was confirmed."
        };
    }

    public Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct)
    {
        RequireEnabled();
        try
        {
            _ = BuildRequest(plan, settings.Value);
            return Task.FromResult<string?>(null);
        }
        catch (InvalidOperationException ex) { return Task.FromResult<string?>(ex.Message); }
    }

    public Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct)
    {
        RequireEnabled();
        return fiscal.SubmitDesktopCreditAsync(BuildRequest(plan, settings.Value), ct);
    }

    internal static TransactMExtRequest BuildRequest(DesktopCreditPlan plan, RevmaxSettings settings)
    {
        static string Number(decimal n) => n.ToString("0.############", CultureInfo.InvariantCulture);
        var request = new TransactMExtRequest
        {
            InvoiceNumber = plan.Receipt.InvoiceNo, OriginalInvoiceNumber = plan.Source.OriginalFiscalNumber,
            Currency = plan.Source.Currency, BranchName = settings.DefaultBranchName,
            InvoiceAmount = plan.Amount, Istatus = "02", InvoiceComment = plan.Receipt.ReceiptNotes,
            CustomerName = plan.Receipt.Buyer?.RegisterName ?? "", CustomerVatNumber = plan.Receipt.Buyer?.VatNumber ?? "",
            CustomerAddress = string.Join(", ", new[] { plan.Receipt.Buyer?.Street, plan.Receipt.Buyer?.City }.Where(v => !string.IsNullOrWhiteSpace(v))),
            CustomerTelephone = plan.Receipt.Buyer?.Phone ?? "", CustomerEmail = plan.Receipt.Buyer?.Email ?? "",
            CustomerBPN = plan.Receipt.Buyer?.Tin ?? "", Cashier = plan.Receipt.Username ?? "",
            refDeviceId = plan.Source.DeviceId, refFiscalDayNo = plan.Source.FiscalDayNo,
            refReceiptGlobalNo = plan.Source.ReceiptGlobalNo,
            ItemsXml = plan.Receipt.Lines.Select((l, index) =>
            {
                var lineNo = plan.Quantities[index].LineNo.ToString(CultureInfo.InvariantCulture);
                // Both names carry it and neither may be blank: the device refuses the whole credit
                // over an empty ITEMNAME2. See RevmaxRequestItem.Name.
                var name = RevmaxRequestItem.Name(l.Name, $"Line {lineNo}");
                return new RevmaxRequestItem
                {
                    HH = (index + 1).ToString(CultureInfo.InvariantCulture), ItemCode = lineNo,
                    ItemName1 = name, ItemName2 = name,
                    Qty = Number(l.Quantity), Price = Number(Math.Abs(l.Price)),
                    Amt = Number(DesktopCreditPlanner.Round(Math.Abs(l.Price) * l.Quantity)),
                    Tax = l.TaxId.ToString(CultureInfo.InvariantCulture), TaxR = Number(l.TaxPercent ?? 0)
                };
            }).ToList(),
            CurrenciesXml = new List<RevmaxRequestCurrency> { new() { Name = plan.Source.Currency, Amount = Number(plan.Amount), Rate = "1" } }
        };
        request.InvoiceTaxAmount = plan.Receipt.Lines.Sum(l => DesktopCreditPlanner.Round(
            Math.Abs(l.Price) * l.Quantity * (l.TaxPercent ?? 0) / (100 + (l.TaxPercent ?? 0))));
        var wireTotal = ((List<RevmaxRequestItem>)request.ItemsXml).Sum(l => DesktopCreditPlanner.Round(
            decimal.Parse(l.Price!, CultureInfo.InvariantCulture) * decimal.Parse(l.Qty!, CultureInfo.InvariantCulture)));
        if (wireTotal != plan.Amount)
            throw new InvalidOperationException("The credit's line amounts cannot be represented exactly in the REVMax request. Review the original receipt's quantities and rounding.");
        return request;
    }

}
