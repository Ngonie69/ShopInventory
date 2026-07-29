using System.Text.Json;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Converts between an SAP transfer request's lines, the held-edit payload and the DTOs.
/// </summary>
public static class TransferRequestEditMapper
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<TransferRequestEditLineDto> lines)
        => JsonSerializer.Serialize(lines, PayloadOptions);

    public static List<TransferRequestEditLineDto> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<TransferRequestEditLineDto>>(json, PayloadOptions) ?? [];
        }
        catch (JsonException)
        {
            // A held edit whose payload cannot be read must not be applied; the caller checks
            // for an empty list and refuses rather than writing a partial document to SAP.
            return [];
        }
    }

    public static List<TransferRequestEditLineDto> FromDocument(InventoryTransferRequest document)
        => (document.StockTransferLines ?? [])
            .Select(line => new TransferRequestEditLineDto
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UoMCode = line.UoMCode
            })
            .ToList();

    /// <summary>
    /// Applies the requested quantities to the document's lines, dropping any line the edit
    /// leaves out. Returns an error when the edit does not describe a change SAP could accept.
    /// </summary>
    public static (List<TransferRequestEditLineDto>? Lines, string? Error) BuildProposedLines(
        InventoryTransferRequest document,
        IReadOnlyList<EditTransferRequestLineDto> requestedLines)
    {
        var existing = FromDocument(document);
        if (existing.Count == 0)
            return (null, "The transfer request has no lines to change.");

        if (requestedLines.Count == 0)
            return (null, "At least one line must be kept.");

        var duplicates = requestedLines
            .GroupBy(line => line.LineNum)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
            return (null, $"Line {string.Join(", ", duplicates)} appears more than once.");

        var proposed = new List<TransferRequestEditLineDto>();
        foreach (var requested in requestedLines)
        {
            var line = existing.FirstOrDefault(item => item.LineNum == requested.LineNum);
            if (line is null)
                return (null, $"Line {requested.LineNum} is not on transfer request {document.DocEntry}.");

            if (requested.Quantity <= 0)
                return (null, $"Line {requested.LineNum} must keep a quantity greater than zero. Remove the line instead.");

            proposed.Add(new TransferRequestEditLineDto
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = requested.Quantity,
                UoMCode = line.UoMCode
            });
        }

        return (proposed.OrderBy(line => line.LineNum).ToList(), null);
    }

    /// <summary>True when the proposal leaves every line exactly as it already is.</summary>
    public static bool IsNoOp(
        IReadOnlyList<TransferRequestEditLineDto> original,
        IReadOnlyList<TransferRequestEditLineDto> proposed)
        => original.Count == proposed.Count &&
           original.All(line => proposed.Any(item => item.LineNum == line.LineNum && item.Quantity == line.Quantity));

    public static IReadOnlyList<(int LineNum, decimal Quantity)> ToSapLines(
        IEnumerable<TransferRequestEditLineDto> lines)
        => lines.Select(line => (line.LineNum, line.Quantity)).ToList();

    public static PendingTransferRequestEditDto ToDto(PendingTransferRequestEditEntity edit) => new()
    {
        Id = edit.Id,
        RequestDocEntry = edit.RequestDocEntry,
        RequestDocNum = edit.RequestDocNum,
        FromWarehouse = edit.FromWarehouse,
        ToWarehouse = edit.ToWarehouse,
        Status = edit.Status,
        CreatedByName = edit.CreatedByName,
        CreatedByRole = edit.CreatedByRole,
        CreatedByUserId = edit.CreatedByUserId,
        CreatedAtUtc = edit.CreatedAtUtc,
        DecidedAtUtc = edit.DecidedAtUtc,
        AppliedAtUtc = edit.AppliedAtUtc,
        Reason = edit.Reason,
        LastError = edit.LastError,
        ApprovalRequestId = edit.ApprovalRequestId,
        OriginalLines = Deserialize(edit.OriginalLinesJson),
        ProposedLines = Deserialize(edit.ProposedLinesJson)
    };
}
