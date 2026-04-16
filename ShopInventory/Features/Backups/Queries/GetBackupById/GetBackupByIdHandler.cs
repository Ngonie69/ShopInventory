using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Queries.GetBackupById;

public sealed class GetBackupByIdHandler(
    IBackupService backupService
) : IRequestHandler<GetBackupByIdQuery, ErrorOr<BackupDto>>
{
    public async Task<ErrorOr<BackupDto>> Handle(
        GetBackupByIdQuery request,
        CancellationToken cancellationToken)
    {
        var backup = await backupService.GetBackupByIdAsync(request.Id, cancellationToken);
        if (backup is null)
            return Errors.Backup.NotFound(request.Id);

        return backup;
    }
}
