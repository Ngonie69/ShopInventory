using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetPagedInvoices;

public sealed record GetPagedInvoicesQuery(
    int Page,
    int PageSize,
    int? DocNum,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<InvoiceListResponseDto>>;
