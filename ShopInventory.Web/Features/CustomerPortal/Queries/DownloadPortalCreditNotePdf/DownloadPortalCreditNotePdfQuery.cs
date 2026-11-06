using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.CustomerPortal.Queries.DownloadPortalCreditNotePdf;

/// <summary>A credit note's PDF for a portal login.</summary>
/// <param name="PortalCardCode">The account the customer signed in as.</param>
/// <param name="DocEntry">The credit note's SAP DocEntry.</param>
/// <param name="CardCode">The account the credit note is on — this login's own or one it is linked to.</param>
public sealed record DownloadPortalCreditNotePdfQuery(
    string PortalCardCode,
    int DocEntry,
    string CardCode
) : IRequest<ErrorOr<byte[]>>;
