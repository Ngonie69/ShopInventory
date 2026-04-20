using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Merchandiser.Commands.BackfillProductDetails;

public sealed record BackfillProductDetailsCommand : IRequest<ErrorOr<int>>;
