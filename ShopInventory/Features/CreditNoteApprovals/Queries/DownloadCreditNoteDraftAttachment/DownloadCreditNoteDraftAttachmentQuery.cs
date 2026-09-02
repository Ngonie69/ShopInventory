using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.DownloadCreditNoteDraftAttachment;

/// <summary>
/// The bytes of one file attached to the draft an approval request holds. Keyed on the request, not
/// the attachment record, so a caller can only reach files of a draft that is in the approval queue.
/// </summary>
public sealed record DownloadCreditNoteDraftAttachmentQuery(int Code, int LineNum) : IRequest<ErrorOr<SapAttachmentDownload>>;
