using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;

/// <summary>
/// One page of a warehouse's products, positioned by <c>Page</c> or, in cursor mode, by <c>After</c>.
/// </summary>
/// <remarks>
/// <c>UseCursor</c> opts in to cursor paging and is off by default, so handsets already in the field
/// keep the offset behaviour they were built against — they cannot all be updated at once, and a page
/// boundary moving under them is the failure this is here to remove.
/// <para>
/// <c>After</c> is the last item code the caller has seen; null reads from the start. Supplying it
/// implies cursor mode even without <c>UseCursor</c>, because falling back to offsets would answer a
/// cursor request with a page positioned somewhere else entirely and say nothing about it.
/// </para>
/// </remarks>
public sealed record GetPagedProductsInWarehouseQuery(
    string WarehouseCode,
    int Page = 1,
    int PageSize = 20,
    string? BusinessPartnerCode = null,
    int? PriceListNum = null,
    bool VanSaleOnly = false,
    bool UseCursor = false,
    string? After = null
) : IRequest<ErrorOr<WarehouseProductsPagedResponseDto>>
{
    /// <summary>Whether this query is positioned by cursor rather than by offset.</summary>
    public bool IsCursorPaged => UseCursor || After is not null;
}
