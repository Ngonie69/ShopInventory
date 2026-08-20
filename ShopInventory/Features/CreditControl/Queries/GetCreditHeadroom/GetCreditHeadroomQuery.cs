using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditControl.Queries.GetCreditHeadroom;

/// <summary>
/// How much room each of these customers has left against the limit that governs it.
/// </summary>
/// <remarks>
/// Answered from the same cached sweep as the over-limit list, so asking about the eight customers
/// on a page of pending orders costs nothing beyond the sweep that was going to happen anyway.
/// </remarks>
public sealed record GetCreditHeadroomQuery(
    IReadOnlyCollection<string> CardCodes,
    bool Refresh = false) : IRequest<ErrorOr<CreditHeadroomResponseDto>>;
