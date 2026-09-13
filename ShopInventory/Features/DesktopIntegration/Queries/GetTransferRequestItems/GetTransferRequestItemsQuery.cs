using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestItems;

/// <summary>
/// The items a till may put on a transfer request: every item SAP's item master flags as a sales item.
/// </summary>
/// <remarks>
/// Not the warehouse's stock. The daily snapshot lists only what a shop holds, so a request screen
/// built from it could never ask for the item a request is most often for — one the shop has run out
/// of. Which items can be asked for is an item-master decision (<c>OITM.U_SalesItem = 'Yes'</c>), the
/// same in every shop; the till joins its own stock and prices onto this list.
/// </remarks>
public sealed record GetTransferRequestItemsQuery : IRequest<ErrorOr<TransferRequestItemsResult>>;

/// <summary>Every requestable item.</summary>
/// <param name="Items">One row per item code, ordered by code.</param>
public sealed record TransferRequestItemsResult(List<TransferRequestItemDto> Items);

/// <summary>One item that can be requested.</summary>
/// <param name="ItemCode">The item, as SAP spells it.</param>
/// <param name="ItemName">The item master's description.</param>
public sealed record TransferRequestItemDto(string ItemCode, string ItemName);
