using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Backups.Queries.GetBackupById;

public sealed record GetBackupByIdQuery(int Id) : IRequest<ErrorOr<BackupDto>>;
