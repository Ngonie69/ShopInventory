using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Documents.Queries.DownloadAttachment;

public sealed record AttachmentDownloadResult(Stream Stream, string FileName, string MimeType);

public sealed record DownloadAttachmentQuery(int Id) : IRequest<ErrorOr<AttachmentDownloadResult>>;
