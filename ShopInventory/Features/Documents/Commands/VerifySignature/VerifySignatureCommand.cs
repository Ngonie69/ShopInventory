using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Documents.Commands.VerifySignature;

public sealed record VerifySignatureCommand(int Id) : IRequest<ErrorOr<bool>>;
