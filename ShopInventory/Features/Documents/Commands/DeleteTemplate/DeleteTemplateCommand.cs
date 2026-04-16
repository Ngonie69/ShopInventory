using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Documents.Commands.DeleteTemplate;

public sealed record DeleteTemplateCommand(int Id) : IRequest<ErrorOr<bool>>;
