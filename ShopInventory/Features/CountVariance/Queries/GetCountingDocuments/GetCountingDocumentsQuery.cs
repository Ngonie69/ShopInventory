using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CountVariance.Queries.GetCountingDocuments;

/// <summary>
/// The SAP inventory counts a variance report can be run on, newest first.
/// </summary>
/// <param name="Status"><c>open</c>, <c>closed</c> or <c>all</c>; open when empty.</param>
/// <param name="Search">A document number, or text in the count's remarks.</param>
public sealed record GetCountingDocumentsQuery(string? Status, string? Search)
    : IRequest<ErrorOr<CountingDocumentListResponseDto>>;
