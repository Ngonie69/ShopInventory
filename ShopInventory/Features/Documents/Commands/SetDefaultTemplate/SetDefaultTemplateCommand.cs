using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Documents.Commands.SetDefaultTemplate;

public sealed record SetDefaultTemplateCommand(int Id) : IRequest<ErrorOr<bool>>;
