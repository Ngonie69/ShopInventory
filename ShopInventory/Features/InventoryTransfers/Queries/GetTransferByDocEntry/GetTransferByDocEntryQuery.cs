using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferByDocEntry;

public sealed record GetTransferByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<InventoryTransferDto>>;
