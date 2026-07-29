using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Commands.RepriceQuotation;

public sealed record RepriceQuotationCommand(int Id, CreateQuotationRequest Request) : IRequest<ErrorOr<QuotationDto>>;