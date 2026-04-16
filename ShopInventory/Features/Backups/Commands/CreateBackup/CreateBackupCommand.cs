using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Backups.Commands.CreateBackup;

public sealed record CreateBackupCommand(
    CreateBackupRequest Request,
    Guid UserId
) : IRequest<ErrorOr<BackupDto>>;
