using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Backups.Commands.RestoreBackup;

public sealed record RestoreBackupCommand(int Id, Guid UserId) : IRequest<ErrorOr<Success>>;
