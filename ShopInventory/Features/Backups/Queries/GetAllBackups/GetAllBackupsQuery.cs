using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Backups.Queries.GetAllBackups;

public sealed record GetAllBackupsQuery() : IRequest<ErrorOr<BackupListResponseDto>>;
