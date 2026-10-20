using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// What happens once a transfer is in its holding warehouse: the depot order it fulfils is closed,
/// and the people who may accept it are told it is coming.
/// </summary>
/// <remarks>
/// Both are secondary. The stock has already moved in SAP, so a failure here is logged and never
/// fails the send.
/// </remarks>
public interface IFactoryTransferFollowUps
{
    Task OnInTransitAsync(FactoryTransferEntity transfer);
}

public sealed class FactoryTransferFollowUps(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    INotificationService notificationService,
    ILogger<FactoryTransferFollowUps> logger) : IFactoryTransferFollowUps
{
    public async Task OnInTransitAsync(FactoryTransferEntity transfer)
    {
        await CloseDepotOrderAsync(transfer);
        if (!FactoryTransferDirections.IsDirect(transfer.Direction))
            await NotifyReceiversAsync(transfer);
    }

    /// <summary>
    /// A depot order copied in the SAP client stays open forever: 29 of the 31 depot requests raised
    /// between 1 September and 19 October 2026 were still open. Loading the truck is what fulfils it.
    /// </summary>
    private async Task CloseDepotOrderAsync(FactoryTransferEntity transfer)
    {
        if (transfer.SourceRequestDocEntry is not int requestDocEntry || transfer.OutSapDocEntry is not int docEntry)
            return;

        try
        {
            await sapClient.CloseInventoryTransferRequestAsync(requestDocEntry, CancellationToken.None);
            await approvalService.MarkGeneratedAsync(
                requestDocEntry, docEntry, transfer.OutSapDocNum ?? 0, transfer.SentByUserId, byAuthorizer: false,
                CancellationToken.None);
            logger.LogInformation("Depot order {RequestDocEntry} closed by factory transfer {Number}", requestDocEntry, transfer.Number);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Factory transfer {Number} is loaded but depot order {RequestDocEntry} could not be closed; close it by hand",
                transfer.Number, requestDocEntry);
        }
    }

    /// <summary>
    /// Addressed by username, under the TransferApproval category: that is what reaches one person and
    /// nobody else. A notification addressed only by user id is broadcast over SignalR to the whole role.
    /// </summary>
    private async Task NotifyReceiversAsync(FactoryTransferEntity transfer)
    {
        try
        {
            var quoted = $"%\"{transfer.DestinationWarehouseCode}\"%";
            var receivers = await context.Users
                .AsNoTracking()
                .Where(user => user.IsActive
                    && user.Id != transfer.SentByUserId
                    && user.AssignedWarehouseCodes != null
                    && EF.Functions.Like(user.AssignedWarehouseCodes, quoted))
                .Select(user => user.Username)
                .ToListAsync();

            var truck = string.IsNullOrWhiteSpace(transfer.VehiclePlate)
                ? string.Empty
                : $" on truck {transfer.VehiclePlate}" + (string.IsNullOrWhiteSpace(transfer.DriverName) ? string.Empty : $" ({transfer.DriverName})");
            var units = transfer.Lines.Sum(line => line.Quantity);

            foreach (var username in receivers)
            {
                await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                {
                    Title = $"{transfer.Number} is on its way to {transfer.DestinationWarehouseCode}",
                    Message = $"{transfer.SentByName} sent {units:0.##} unit(s) from {transfer.SourceWarehouseCode}{truck}. "
                        + "Count it when it arrives, then accept it.",
                    Type = "Info",
                    Category = "TransferApproval",
                    EntityType = "FactoryTransfer",
                    EntityId = transfer.Id.ToString(),
                    ActionUrl = "/factory-transfers",
                    TargetUsername = username
                }, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not tell the receivers of factory transfer {Number}", transfer.Number);
        }
    }
}
