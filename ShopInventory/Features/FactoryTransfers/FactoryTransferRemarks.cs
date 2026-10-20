using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// The Comments written on each SAP leg of a factory transfer: the transfer, who sent it and when,
/// who settled it and when, and the reasons.
/// </summary>
/// <remarks>
/// The audit trail goes first and the free-text reasons take what is left of SAP's 254 characters,
/// so a long reason can never push out the record of who moved the stock.
/// </remarks>
public static class FactoryTransferRemarks
{
    public const int MaxLength = 254;

    private const string Separator = " | ";

    public static string ForOutLeg(FactoryTransferEntity transfer)
        => Compose(Trail(transfer, includeDecision: false), Reasons(transfer, includeDecision: false));

    public static string ForInLeg(FactoryTransferEntity transfer)
        => Compose(Trail(transfer, includeDecision: true), Reasons(transfer, includeDecision: true));

    private static List<string> Trail(FactoryTransferEntity transfer, bool includeDecision)
    {
        var department = FactoryDepartments.Find(transfer.DepartmentCode)?.Name ?? transfer.DepartmentCode;
        var direction = transfer.Direction == FactoryTransferDirections.Return ? "return" : "release";

        var trail = new List<string>
        {
            $"{transfer.Number} {department} {direction} {transfer.SourceWarehouseCode} to {transfer.DestinationWarehouseCode}",
            $"Sent by {transfer.SentByName} {FormatTime(transfer.SentAtUtc)}"
        };

        if (includeDecision && transfer.DecidedAtUtc is DateTime decidedAt)
            trail.Add($"{DecisionVerb(transfer)} by {transfer.DecidedByName} {FormatTime(decidedAt)}");

        return trail;
    }

    private static List<(string Label, string Text)> Reasons(FactoryTransferEntity transfer, bool includeDecision)
    {
        var reasons = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(transfer.Reason))
            reasons.Add(("Reason: ", transfer.Reason.Trim()));
        if (includeDecision && !string.IsNullOrWhiteSpace(transfer.DecisionReason))
            reasons.Add(("Note: ", transfer.DecisionReason.Trim()));
        return reasons;
    }

    private static string DecisionVerb(FactoryTransferEntity transfer)
    {
        if (transfer.Decision == FactoryTransferDecisions.Rejected)
            return "Rejected";
        if (transfer.Decision == FactoryTransferDecisions.Recalled)
            return "Recalled";

        var shortAccepted = transfer.Lines.Any(line => (line.AcceptedQuantity ?? 0) < line.Quantity);
        return shortAccepted ? "Part-accepted" : "Accepted";
    }

    private static string Compose(List<string> trail, List<(string Label, string Text)> reasons)
    {
        var text = Truncate(string.Join(Separator, trail), MaxLength);

        foreach (var (label, reason) in reasons)
        {
            var budget = MaxLength - text.Length - Separator.Length - label.Length;
            if (budget <= 0)
                break;
            text = $"{text}{Separator}{label}{Truncate(reason, budget)}";
        }

        return text;
    }

    private static string FormatTime(DateTime utc)
        => AuditService.ToCAT(utc).ToString("dd MMM yyyy HH:mm");

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length];
}
