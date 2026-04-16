using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Backups.Queries.GetBackupStats;

public sealed record GetBackupStatsQuery() : IRequest<ErrorOr<BackupStatsDto>>;
