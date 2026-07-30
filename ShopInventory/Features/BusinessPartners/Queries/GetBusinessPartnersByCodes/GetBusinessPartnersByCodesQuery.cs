using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByCodes;

/// <summary>
/// Resolves a known set of card codes in one go, for callers that would otherwise loop over
/// <c>GetBusinessPartnerByCodeQuery</c> — one SAP request per customer.
/// </summary>
public sealed record GetBusinessPartnersByCodesQuery(IReadOnlyCollection<string> CardCodes)
    : IRequest<ErrorOr<BusinessPartnerListResponseDto>>;
