using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffItems;

/// <summary>
/// The items a write-off may name: every active product in the Web's own catalogue, read from
/// PostgreSQL and never from SAP.
/// </summary>
/// <remarks>
/// Deliberately not scoped to a warehouse. The page used to ask the API for the items holding
/// stock in the chosen warehouse, which walks SAP's stock and batch tables page by page for every
/// warehouse an operator so much as clicks on — the single most expensive read on a page that
/// only needed a code and a name to put in a picker. Whether the warehouse actually holds the
/// item is settled where it matters: the batch picker reads that one item's batches from SAP, and
/// the API refuses the goods issue when the warehouse is short.
/// </remarks>
public sealed record GetStockWriteOffItemsQuery : IRequest<ErrorOr<GetStockWriteOffItemsResult>>;
