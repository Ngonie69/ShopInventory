using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryLog;

/// <summary>
/// The delivery log, newest first, for the administrators' page.
/// </summary>
/// <param name="Status">A status name, or "attention" for everything that needs a person.</param>
/// <param name="Trigger">Auto, Manual or Counter.</param>
/// <param name="Search">A document number, card code or customer name.</param>
/// <param name="FromDate">First CAT day, inclusive.</param>
/// <param name="ToDate">Last CAT day, inclusive.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">Rows a page, at most 200.</param>
public sealed record GetCustomerDocumentDeliveryLogQuery(
    string? Status,
    string? Trigger,
    string? Search,
    DateTime? FromDate,
    DateTime? ToDate,
    int Page = 1,
    int PageSize = 50) : IRequest<ErrorOr<CustomerDocumentDeliveryPageDto>>;
