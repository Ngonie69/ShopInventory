using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseQuotations.Queries.GetPurchaseQuotationByDocEntry;

public sealed record GetPurchaseQuotationByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<PurchaseQuotationDto>>;