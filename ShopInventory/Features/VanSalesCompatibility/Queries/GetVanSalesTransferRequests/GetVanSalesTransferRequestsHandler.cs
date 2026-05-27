using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestsByWarehouse;
using ShopInventory.Models;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesTransferRequests;

public sealed class GetVanSalesTransferRequestsHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<GetVanSalesTransferRequestsQuery, ErrorOr<List<VanSalesLegacyInventoryOrderDto>>>
{
    public async Task<ErrorOr<List<VanSalesLegacyInventoryOrderDto>>> Handle(
        GetVanSalesTransferRequestsQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required to load inventory requests.");
        }

        var requestsResult = await mediator.Send(new GetTransferRequestsByWarehouseQuery(warehouseCode), cancellationToken);
        if (requestsResult.IsError)
        {
            return requestsResult.Errors;
        }

        var allRequests = requestsResult.Value.TransferRequests ?? new List<InventoryTransferRequestDto>();
        var displayName = string.Join(" ", new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

        var requesterScoped = allRequests
            .Where(request => MatchesRequester(request, user.Username, user.Email, displayName))
            .ToList();

        var requestsToMap = requesterScoped.Count > 0 ? requesterScoped : allRequests;
        var docEntryKeys = requestsToMap
            .Select(request => request.DocEntry.ToString())
            .ToList();

        var auditActions = await db.AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityType == "TransferRequest" &&
                log.EntityId != null &&
                docEntryKeys.Contains(log.EntityId) &&
                (log.Action == AuditActions.ConvertTransferRequest || log.Action == AuditActions.CloseTransferRequest))
            .OrderByDescending(log => log.Timestamp)
            .ToListAsync(cancellationToken);

        var latestActionByEntityId = auditActions
            .GroupBy(log => log.EntityId!)
            .ToDictionary(group => group.Key, group => group.First().Action);

        return requestsToMap
            .OrderByDescending(request => VanSalesCompatibilityMapper.ParseLegacyDate(request.DocDate) ?? DateTime.MinValue)
            .ThenByDescending(request => request.DocEntry)
            .Select(request => VanSalesCompatibilityMapper.MapLegacyTransferRequest(
                request,
                MapLegacyStatus(request.DocEntry, request.DocumentStatus, latestActionByEntityId)))
            .ToList();
    }

    private static bool MatchesRequester(
        InventoryTransferRequestDto request,
        string username,
        string? email,
        string displayName)
    {
        if (!string.IsNullOrWhiteSpace(email) &&
            string.Equals(request.RequesterEmail, email, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(displayName) &&
            string.Equals(request.RequesterName, displayName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(username) &&
            string.Equals(request.RequesterName, username, StringComparison.OrdinalIgnoreCase);
    }

    private static int MapLegacyStatus(
        int docEntry,
        string? documentStatus,
        IReadOnlyDictionary<string, string> latestActionByEntityId)
    {
        var key = docEntry.ToString();
        if (latestActionByEntityId.TryGetValue(key, out var action))
        {
            return string.Equals(action, AuditActions.CloseTransferRequest, StringComparison.OrdinalIgnoreCase)
                ? 3
                : 2;
        }

        return string.Equals(documentStatus, "bost_Close", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(documentStatus, "Closed", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 0;
    }
}