using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Queries.GetUnprocessedInvoicesSummary;

public sealed record GetUnprocessedInvoicesSummaryQuery() : IRequest<ErrorOr<UnprocessedInvoicesSummaryResponse>>;
