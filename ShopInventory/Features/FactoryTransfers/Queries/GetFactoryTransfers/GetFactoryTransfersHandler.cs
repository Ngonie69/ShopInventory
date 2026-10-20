using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfers;

/// <summary>
/// The factory transfers the caller may see, newest first, with the count of those waiting on them.
/// </summary>
public sealed class GetFactoryTransfersHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement)
    : IRequestHandler<GetFactoryTransfersQuery, ErrorOr<FactoryTransferListResponseDto>>
{
    public async Task<ErrorOr<FactoryTransferListResponseDto>> Handle(
        GetFactoryTransfersQuery query,
        CancellationToken cancellationToken)
    {
        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, query.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;

        var transfers = FactoryTransferReader.Visible(context.FactoryTransfers.AsNoTracking(), actor);
        var awaitingCount = await FactoryTransferReader.AwaitingDecision(transfers, actor).CountAsync(cancellationToken);

        var view = query.View?.Trim();
        if (string.IsNullOrEmpty(view) || string.Equals(view, GetFactoryTransfersQuery.All, StringComparison.OrdinalIgnoreCase))
        {
            // every visible transfer
        }
        else if (string.Equals(view, GetFactoryTransfersQuery.Awaiting, StringComparison.OrdinalIgnoreCase))
        {
            transfers = FactoryTransferReader.AwaitingDecision(transfers, actor);
        }
        else if (string.Equals(view, GetFactoryTransfersQuery.Open, StringComparison.OrdinalIgnoreCase))
        {
            var open = FactoryTransferStatuses.Open.ToList();
            transfers = transfers.Where(transfer => open.Contains(transfer.Status));
        }
        else
        {
            var status = FactoryTransferStatuses.All.First(item => string.Equals(item, view, StringComparison.OrdinalIgnoreCase));
            transfers = transfers.Where(transfer => transfer.Status == status);
        }

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.ToUpperInvariant()}%";
            var numberId = ParseNumber(search);
            transfers = transfers.Where(transfer =>
                transfer.Id == numberId
                || EF.Functions.Like(transfer.SentByName.ToUpper(), pattern)
                || EF.Functions.Like(transfer.SourceWarehouseCode.ToUpper(), pattern)
                || EF.Functions.Like(transfer.DestinationWarehouseCode.ToUpper(), pattern)
                || (transfer.VehiclePlate != null && EF.Functions.Like(transfer.VehiclePlate.ToUpper(), pattern))
                || (transfer.DriverName != null && EF.Functions.Like(transfer.DriverName.ToUpper(), pattern))
                || transfer.Lines.Any(line => EF.Functions.Like(line.ItemCode.ToUpper(), pattern)));
        }

        var total = await transfers.CountAsync(cancellationToken);
        var rows = await transfers
            .OrderByDescending(transfer => transfer.SentAtUtc)
            .ThenByDescending(transfer => transfer.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(FactoryTransferReader.RowProjection)
            .ToListAsync(cancellationToken);

        var totals = await FactoryTransferReader.TotalsAsync(context, rows.Select(row => row.Id).ToList(), cancellationToken);

        return new FactoryTransferListResponseDto
        {
            Items = rows.Select(row => FactoryTransferReader.ToSummary(row, actor, totals.GetValueOrDefault(row.Id))).ToList(),
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize,
            AwaitingMeCount = awaitingCount
        };
    }

    /// <summary>"FT-000123", "ft123" or "123" all find transfer 123; anything else matches no id.</summary>
    private static int ParseNumber(string search)
    {
        var digits = search.ToUpperInvariant().Replace("FT", string.Empty).Replace("-", string.Empty).Trim();
        return int.TryParse(digits, out var id) ? id : -1;
    }
}
