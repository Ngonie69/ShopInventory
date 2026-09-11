using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.DownloadCreditNoteDraftAttachment;

/// <summary>
/// The bytes of one file attached to the draft an approval request holds. Keyed on the request, not
/// the attachment record, so a caller can only reach files of a draft that is in the approval queue.
/// </summary>
/// <param name="Code">The approval request.</param>
/// <param name="LineNum">The attachment line on its draft.</param>
/// <param name="CallerUserId">The caller's account; a stage-scoped one is refused a request outside its stages.</param>
public sealed record DownloadCreditNoteDraftAttachmentQuery(int Code, int LineNum, Guid? CallerUserId = null) : IRequest<ErrorOr<SapAttachmentDownload>>;
