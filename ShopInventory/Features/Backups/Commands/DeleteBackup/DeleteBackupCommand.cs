using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Backups.Commands.DeleteBackup;

public sealed record DeleteBackupCommand(int Id) : IRequest<ErrorOr<Deleted>>;
