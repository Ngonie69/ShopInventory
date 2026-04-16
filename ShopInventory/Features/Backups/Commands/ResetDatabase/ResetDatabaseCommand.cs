using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Backups.Commands.ResetDatabase;

public sealed record ResetDatabaseCommand(Guid UserId, string Caller) : IRequest<ErrorOr<Success>>;
