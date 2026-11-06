using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CreditNotes.Queries.DownloadCreditNotePdf;

/// <summary>A credit memo as a PDF.</summary>
/// <param name="DocEntry">The memo's SAP DocEntry.</param>
/// <param name="CardCode">
/// The account the caller expects it on. The customer portal always sends it, and a memo on any other
/// account answers exactly as one that does not exist. Null for staff, who may print any memo.
/// </param>
public sealed record DownloadCreditNotePdfQuery(
    int DocEntry,
    string? CardCode = null
) : IRequest<ErrorOr<CreditNotePdfFile>>;
