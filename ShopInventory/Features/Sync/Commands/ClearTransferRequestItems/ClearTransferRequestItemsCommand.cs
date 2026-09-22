using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.ClearTransferRequestItems;

/// <summary>
/// Drops the hour-long hold on the list a till raises transfer requests from, so the next till to
/// open its request screen reads <c>U_SalesItem</c> and <c>U_VanSale</c> from SAP again.
/// </summary>
/// <remarks>
/// Sent by the Products sync in Settings. An item flagged in the item master is otherwise missing
/// from every till for up to an hour, and a refreshed product list elsewhere suggests it should not be.
/// The last list read is kept, so a till opening the screen during a SAP outage is still served.
/// </remarks>
public sealed record ClearTransferRequestItemsCommand() : IRequest<ErrorOr<Success>>;
