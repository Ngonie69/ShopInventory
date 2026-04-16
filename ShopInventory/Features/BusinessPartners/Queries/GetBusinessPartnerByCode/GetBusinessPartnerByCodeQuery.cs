using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerByCode;

public sealed record GetBusinessPartnerByCodeQuery(string CardCode) : IRequest<ErrorOr<BusinessPartnerDto>>;
