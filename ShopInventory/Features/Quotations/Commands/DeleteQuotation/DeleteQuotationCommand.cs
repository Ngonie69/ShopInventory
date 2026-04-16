using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Quotations.Commands.DeleteQuotation;

public sealed record DeleteQuotationCommand(int Id) : IRequest<ErrorOr<Deleted>>;
