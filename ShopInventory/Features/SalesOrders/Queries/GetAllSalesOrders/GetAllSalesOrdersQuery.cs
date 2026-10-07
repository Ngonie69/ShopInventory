using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;

/// <param name="Page">The 1-based page to return.</param>
/// <param name="PageSize">Orders per page, at most 10,000.</param>
/// <param name="Status">The status the list shows: Pending includes an approved order that has not reached SAP.</param>
/// <param name="CardCode">Matches anywhere in the customer code or name.</param>
/// <param name="FromDate">Earliest order date.</param>
/// <param name="ToDate">Latest order date, inclusive.</param>
/// <param name="Source">Where the orders came from. Set, the list is answered from the local tables alone.</param>
/// <param name="Search">Matches the order number or customer reference, or a SAP number exactly.</param>
/// <param name="VanSalesUsersOnly">True for van-sales orders only, false for everything else.</param>
/// <param name="OpenOnly">Only orders still waiting on someone: draft, pending, on hold, or approved and not posted.</param>
/// <param name="IncludeSummary">Add all-time counts for the source and view, whatever the other filters.</param>
/// <param name="Columns">The Mobile Orders column filters. Local list only.</param>
/// <param name="Sort">The column to sort by. Local list only; id descending breaks ties.</param>
/// <param name="SortDescending">Sort direction.</param>
/// <param name="KeepOpenOrders">With <paramref name="FromDate"/>, still return an open order dated before it.</param>
public sealed record GetAllSalesOrdersQuery(
    int Page,
    int PageSize,
    SalesOrderStatus? Status,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    SalesOrderSource? Source,
    string? Search = null,
    bool? VanSalesUsersOnly = null,
    bool OpenOnly = false,
    bool IncludeSummary = false,
    SalesOrderColumnFilters? Columns = null,
    SalesOrderListSort Sort = SalesOrderListSort.Ordered,
    bool SortDescending = true,
    bool KeepOpenOrders = false
) : IRequest<ErrorOr<SalesOrderListResponseDto>>;

/// <summary>
/// The Mobile Orders column filters, matched the way the page matched them in memory before it paged
/// on the server: text filters anywhere in the value and ignoring case, dates on the calendar day.
/// </summary>
/// <param name="OrderNumber">Part of the order number.</param>
/// <param name="OrderDate">The order date's day.</param>
/// <param name="DeliveryDate">The delivery date's day.</param>
/// <param name="Currency">The currency, exactly but ignoring case.</param>
/// <param name="Total">Part of the document total; thousands separators are ignored.</param>
/// <param name="SapDocNum">Part of the SAP document number.</param>
public sealed record SalesOrderColumnFilters(
    string? OrderNumber = null,
    DateTime? OrderDate = null,
    DateTime? DeliveryDate = null,
    string? Currency = null,
    string? Total = null,
    string? SapDocNum = null);

/// <summary>The Mobile Orders sortable columns.</summary>
public enum SalesOrderListSort
{
    Ordered,
    Number,
    Customer,
    Delivery,
    Status,
    Total,
    SapDoc
}
