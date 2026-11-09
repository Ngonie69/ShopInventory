using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// Turns the quantities a person typed against an approved draft into the PATCH that changes it,
/// or says why it cannot.
/// </summary>
/// <remarks>
/// Quantities may go up or down, never to zero: a line comes off the credit note in SAP, not here.
/// A line holding one batch moves that batch by the same amount, in the inventory unit batches are
/// counted in. A line split across several batches, or carrying serial numbers, cannot say which of
/// them changed, so it is refused rather than guessed at.
/// </remarks>
public static class CreditNoteDraftQuantityPlan
{
    /// <summary>The most decimal places a quantity may carry; SAP's quantity columns hold six.</summary>
    public const int MaxDecimals = 6;

    public static ErrorOr<IReadOnlyList<CreditNoteLineQuantityChangeDto>> Validate(
        IReadOnlyList<SAPCreditNoteLine> lines,
        IReadOnlyList<CreditNoteLineQuantityDto>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return Array.Empty<CreditNoteLineQuantityChangeDto>();
        }

        var byLine = lines.ToDictionary(line => line.LineNum);
        var seen = new HashSet<int>();
        var changes = new List<CreditNoteLineQuantityChangeDto>();

        foreach (var edit in requested)
        {
            if (!seen.Add(edit.LineNum))
            {
                return Errors.CreditNoteApproval.InvalidQuantities($"Line {edit.LineNum + 1} is given more than once.");
            }

            if (!byLine.TryGetValue(edit.LineNum, out var line))
            {
                return Errors.CreditNoteApproval.InvalidQuantities($"The draft has no line {edit.LineNum + 1}.");
            }

            if (edit.Quantity <= 0m)
            {
                return Errors.CreditNoteApproval.InvalidQuantities(
                    $"{Name(line)} needs a quantity above zero. To take a line off, change the draft in SAP.");
            }

            if (decimal.Round(edit.Quantity, MaxDecimals) != edit.Quantity)
            {
                return Errors.CreditNoteApproval.InvalidQuantities(
                    $"{Name(line)}: SAP holds at most {MaxDecimals} decimal places.");
            }

            if (edit.Quantity != line.Quantity)
            {
                changes.Add(new CreditNoteLineQuantityChangeDto
                {
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    From = line.Quantity,
                    To = edit.Quantity
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// The SAP change for each line, its batch moved alongside. <paramref name="allocations"/> is the
    /// whole-draft read, the only one that carries batches.
    /// </summary>
    public static ErrorOr<IReadOnlyList<SapDraftLineQuantityChange>> Build(
        IReadOnlyList<CreditNoteLineQuantityChangeDto> changes,
        SAPDraftAllocations allocations)
    {
        var byLine = (allocations.DocumentLines ?? []).ToDictionary(line => line.LineNum);
        var result = new List<SapDraftLineQuantityChange>(changes.Count);

        foreach (var change in changes)
        {
            if (!byLine.TryGetValue(change.LineNum, out var line))
            {
                return Errors.CreditNoteApproval.InvalidQuantities($"The draft has no line {change.LineNum + 1}.");
            }

            var label = change.ItemCode ?? $"Line {change.LineNum + 1}";

            if (line.SerialNumbers is { Count: > 0 })
            {
                return Errors.CreditNoteApproval.QuantityNotEditable(
                    $"{label} carries serial numbers, so its quantity can only be changed in SAP.");
            }

            var batches = (line.BatchNumbers ?? []).Where(batch => !string.IsNullOrWhiteSpace(batch.BatchNumber)).ToList();
            if (batches.Count > 1)
            {
                return Errors.CreditNoteApproval.QuantityNotEditable(
                    $"{label} is split across {batches.Count} batches, so its quantity can only be changed in SAP.");
            }

            var deltas = new List<SapDraftBatchDelta>();
            if (batches.Count == 1)
            {
                // Batches count in the inventory unit; the line may be in a sales unit of several.
                var factor = line.Quantity != 0m && line.InventoryQuantity is { } inventory && inventory != 0m
                    ? inventory / line.Quantity
                    : 1m;
                var delta = decimal.Round((change.To - line.Quantity) * factor, MaxDecimals);
                if (batches[0].Quantity + delta <= 0m)
                {
                    return Errors.CreditNoteApproval.QuantityNotEditable(
                        $"{label}: its batch {batches[0].BatchNumber} holds less than the change takes off, so change it in SAP.");
                }

                if (delta != 0m)
                {
                    deltas.Add(new SapDraftBatchDelta(batches[0].BatchNumber!, delta));
                }
            }

            result.Add(new SapDraftLineQuantityChange(change.LineNum, change.To, deltas));
        }

        return result;
    }

    public static string Describe(IReadOnlyList<CreditNoteLineQuantityChangeDto> changes) =>
        string.Join(", ", changes.Select(change =>
            $"{change.ItemCode ?? $"line {change.LineNum + 1}"} {Format(change.From)} → {Format(change.To)}"));

    private static string Name(SAPCreditNoteLine line) => line.ItemCode ?? $"Line {line.LineNum + 1}";

    private static string Format(decimal quantity) => quantity.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
