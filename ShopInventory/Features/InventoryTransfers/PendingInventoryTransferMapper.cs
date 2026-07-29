using System.Text.Json;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Converts between the held transfer payload and the DTOs the API exposes.
/// </summary>
public static class PendingInventoryTransferMapper
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static string SerializePayload(CreateInventoryTransferRequest request)
        => JsonSerializer.Serialize(request, PayloadOptions);

    /// <summary>
    /// Rehydrates the original transfer payload. Throws <see cref="InvalidOperationException"/>
    /// if the stored JSON cannot be read, which would otherwise post an empty transfer.
    /// </summary>
    public static CreateInventoryTransferRequest DeserializePayload(PendingInventoryTransferEntity pending)
    {
        CreateInventoryTransferRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CreateInventoryTransferRequest>(pending.PayloadJson, PayloadOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The stored payload for pending transfer {pending.Id} could not be read: {exception.Message}", exception);
        }

        if (payload?.Lines is null || payload.Lines.Count == 0)
            throw new InvalidOperationException($"The stored payload for pending transfer {pending.Id} has no transfer lines.");

        return payload;
    }

    public static PendingInventoryTransferDto ToDto(PendingInventoryTransferEntity pending, bool includeLines = true)
    {
        var dto = new PendingInventoryTransferDto
        {
            Id = pending.Id,
            DraftNumber = pending.DraftNumber,
            FromWarehouse = pending.FromWarehouse,
            ToWarehouse = pending.ToWarehouse,
            Status = pending.Status,
            CreatedByName = pending.CreatedByName,
            CreatedByRole = pending.CreatedByRole,
            CreatedByUserId = pending.CreatedByUserId,
            CreatedAtUtc = pending.CreatedAtUtc,
            DecidedAtUtc = pending.DecidedAtUtc,
            LineCount = pending.LineCount,
            TotalQuantity = pending.TotalQuantity,
            Comments = pending.Comments,
            DocDate = pending.DocDate,
            DueDate = pending.DueDate,
            SapDocEntry = pending.SapDocEntry,
            SapDocNum = pending.SapDocNum,
            PostedAtUtc = pending.PostedAtUtc,
            LastError = pending.LastError,
            ApprovalRequestId = pending.ApprovalRequestId
        };

        if (!includeLines)
            return dto;

        try
        {
            var payload = DeserializePayload(pending);
            dto.Lines = payload.Lines!
                .Select((line, index) => new PendingInventoryTransferLineDto
                {
                    LineNum = index,
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    UoMCode = line.UoMCode,
                    FromWarehouseCode = line.FromWarehouseCode ?? payload.FromWarehouse,
                    ToWarehouseCode = line.ToWarehouseCode ?? payload.ToWarehouse
                })
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // A summary without lines is still useful; the post itself will surface the problem.
        }

        return dto;
    }
}
