using ErrorOr;
using MediatR;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestStock;

/// <summary>
/// What the source warehouse holds of each item on a transfer request, read live from SAP.
/// </summary>
/// <remarks>
/// What a person converting a request needs before they press the button: converting posts a
/// transfer straight to SAP, and a line the depot cannot fill fails the whole document. The
/// figures let them cut a line down or take it out first.
/// <para><paramref name="FromWarehouse"/> checks every line against another warehouse, for an edit
/// that is moving the request's source; <paramref name="ExtraItemCodes"/> are items not on the request
/// yet, for an edit that is adding them.</para>
/// </remarks>
public sealed record GetTransferRequestStockQuery(
    int DocEntry,
    string? FromWarehouse = null,
    IReadOnlyList<string>? ExtraItemCodes = null) : IRequest<ErrorOr<TransferRequestStockResult>>;
