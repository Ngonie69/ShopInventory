using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CountVariance.Queries.GetCountingDocuments;

/// <param name="Status"><c>open</c>, <c>closed</c> or <c>all</c>.</param>
/// <param name="Search">A document number, or text in the count's remarks.</param>
public sealed record GetCountingDocumentsQuery(string Status, string? Search)
    : IRequest<ErrorOr<List<CountingDocumentSummary>>>;
