using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Merchandiser.Commands.BackfillProductDetails;

public sealed record BackfillProductDetailsCommand : IRequest<ErrorOr<int>>;