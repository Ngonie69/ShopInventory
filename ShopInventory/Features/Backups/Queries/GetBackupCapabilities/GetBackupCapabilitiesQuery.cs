using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Backups.Queries.GetBackupCapabilities;

public sealed record GetBackupCapabilitiesQuery() : IRequest<ErrorOr<BackupCapabilitiesDto>>;