using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>Only quantities and a reason come from the client; fiscal values come from the original receipt.</summary>
public static class DesktopCreditPlanner
{
    public static DesktopCreditPlan Build(DesktopCreditSource source, CreateDesktopCreditRequest request,
        IReadOnlyDictionary<int, decimal> reserved, decimal reservedAmount, DateTime now)
    {
        if (!Guid.TryParseExact(request.RequestKey, "N", out _))
            throw new InvalidOperationException("A stable request key is required.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
            throw new InvalidOperationException("Enter a reason of at most 500 characters.");
        if (request.Lines is not { Count: > 0 } ||
            request.Lines.Select(l => l.LineNo).Distinct().Count() != request.Lines.Count)
            throw new InvalidOperationException("Select each original receipt line at most once.");
        var lines = new List<LineApiRequest>();
        foreach (var selection in request.Lines.OrderBy(l => l.LineNo))
        {
            var original = source.Lines.SingleOrDefault(l => l.LineNo == selection.LineNo)
                ?? throw new InvalidOperationException("A selected line is not on the original receipt.");
            var remaining = original.Quantity - reserved.GetValueOrDefault(original.LineNo);
            if (selection.Quantity <= 0 || selection.Quantity > remaining ||
                decimal.Round(selection.Quantity, 6) != selection.Quantity)
                throw new InvalidOperationException($"Invalid quantity for {original.Name}; {remaining} remains available.");
            lines.Add(new LineApiRequest
            {
                Name = original.Name, Quantity = selection.Quantity, Price = -original.UnitPrice,
                TaxId = original.TaxId, TaxPercent = original.TaxPercent,
                TaxCode = original.TaxCode, HsCode = original.HsCode
            });
        }
        var amount = lines.Sum(l => Round(Math.Abs(l.Price) * l.Quantity));
        if (amount <= 0 || amount + reservedAmount + source.ExternalCreditedAmount > source.OriginalTotal)
            throw new InvalidOperationException("The credit exceeds the original receipt's remaining balance.");
        if (source.DeviceId <= 0 || source.FiscalDayNo <= 0 || source.ReceiptGlobalNo <= 0)
            throw new InvalidOperationException("The original device, fiscal day and receipt number are required.");
        return new DesktopCreditPlan(source, request.Lines.OrderBy(l => l.LineNo).ToList(),
            new SubmitReceiptApiRequest
            {
                InvoiceNo = $"DCN-{request.RequestKey}", ReceiptType = ReceiptType.CreditNote,
                Currency = source.Currency, ReceiptDate = now, TaxInclusive = true,
                PaymentType = MoneyType.Credit, PaymentAmount = -amount, Lines = lines,
                ReceiptNotes = request.Reason.Trim(), ReceiptPrintForm = ReceiptPrintForm.InvoiceA4,
                Buyer = source.Buyer,
                CreditDebitNote = new CreditDebitNoteApiRequest
                {
                    DeviceID = source.DeviceId, FiscalDayNo = source.FiscalDayNo,
                    ReceiptGlobalNo = source.ReceiptGlobalNo
                }
            }, amount);
    }

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
