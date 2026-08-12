using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoicesByCustomer;

public sealed record GetInvoicesByCustomerQuery(
    string CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    int? Page,
    int? PageSize,
    Guid? RequestingUserId = null,
    bool RestrictToAssignedCustomers = false,

    // Lines are off by default: a list only needs the headers, and asking SAP to expand
    // DocumentLines cuts the page size and makes the walk several times slower. Callers that
    // aggregate by item (the customer portal's item summary) ask for them explicitly.
    bool IncludeLines = false
) : IRequest<ErrorOr<InvoiceDateResponseDto>>;
