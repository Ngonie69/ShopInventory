using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestByDocEntry;

public sealed record GetTransferRequestByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<InventoryTransferRequestDto>>;
