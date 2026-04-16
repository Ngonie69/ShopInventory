using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.GetPaymentTerms;

public sealed record GetPaymentTermsQuery(int GroupNumber) : IRequest<ErrorOr<PaymentTermsDto>>;
