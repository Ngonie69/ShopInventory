using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Backups.Queries.DownloadBackup;

public sealed record DownloadBackupResult(Stream FileStream, string FileName, string ContentType);

public sealed record DownloadBackupQuery(int Id) : IRequest<ErrorOr<DownloadBackupResult>>;
