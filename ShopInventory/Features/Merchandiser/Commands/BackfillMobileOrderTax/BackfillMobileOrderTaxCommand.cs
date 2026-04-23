using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Merchandiser.Commands.BackfillMobileOrderTax;

public sealed record BackfillMobileOrderTaxCommand : IRequest<ErrorOr<BackfillMobileOrderTaxResult>>;