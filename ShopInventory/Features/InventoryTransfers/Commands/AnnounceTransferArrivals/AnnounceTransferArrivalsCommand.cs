using ErrorOr;
using MediatR;

namespace ShopInventory.Features.InventoryTransfers.Commands.AnnounceTransferArrivals;

/// <summary>One scheduled pass telling tills about the stock transfers SAP has booked into their warehouse.</summary>
public sealed record AnnounceTransferArrivalsCommand(DateTime UtcNow) : IRequest<ErrorOr<AnnounceTransferArrivalsResult>>;
