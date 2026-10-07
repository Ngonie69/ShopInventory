using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetPagedInvoices;

/// <param name="Page">The 1-based page.</param>
/// <param name="PageSize">Invoices per page: up to 200 unfiltered, 5,000 filtered.</param>
/// <param name="DocNum">An exact doc number.</param>
/// <param name="CardCode">An exact customer code.</param>
/// <param name="FromDate">Earliest document date.</param>
/// <param name="ToDate">Latest document date, inclusive.</param>
/// <param name="VanSalesOnly">True for van invoices only, false for none of them.</param>
/// <param name="Search">The page's quick filter: an exact doc number, or text in the customer code or name.</param>
/// <param name="FiscalStatus">"Fiscalised", "Not Fiscalised" or "Unknown". Answered from a scan of the matches; see the handler.</param>
/// <param name="IncludeSummary">Add totals over every match.</param>
/// <param name="RefreshScan">Read SAP again rather than use a fiscal scan from the last two minutes.</param>
/// <param name="FiscalisableOnly">Every match that could be fiscalised, unpaged, for the page's "Select all".</param>
public sealed record GetPagedInvoicesQuery(
    int Page,
    int PageSize,
    int? DocNum,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    bool? VanSalesOnly = null,
    string? Search = null,
    string? FiscalStatus = null,
    bool IncludeSummary = false,
    bool RefreshScan = false,
    bool FiscalisableOnly = false
) : IRequest<ErrorOr<InvoiceListResponseDto>>;
