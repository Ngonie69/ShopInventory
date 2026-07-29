using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Crates.Commands.DeleteCratePod;

public sealed record DeleteCratePodCommand(int CratePodSubmissionId, Guid? UserId) : IRequest<ErrorOr<bool>>;