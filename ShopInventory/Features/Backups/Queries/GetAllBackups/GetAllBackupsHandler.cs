using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Queries.GetAllBackups;

public sealed class GetAllBackupsHandler(
    IBackupService backupService
) : IRequestHandler<GetAllBackupsQuery, ErrorOr<BackupListResponseDto>>
{
    public async Task<ErrorOr<BackupListResponseDto>> Handle(
        GetAllBackupsQuery request,
        CancellationToken cancellationToken)
    {
        var result = await backupService.GetAllBackupsAsync(cancellationToken);
        return result;
    }
}
