using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSalesToSap;

/// <summary>
/// Posts a chosen set of sales to SAP, one invoice each.
/// </summary>
/// <remarks>
/// <para>
/// A set the caller names, not a filter the server re-runs. Handing over a date range would mean the
/// rows posted are decided by a query run after the operator stopped looking, and "post these four"
/// would silently become "post the nine that match by the time it executes".
/// </para>
/// <para>
/// Not a transaction, and could not be one: each sale becomes its own SAP document and SAP has no
/// way to unwind the first four when the fifth is refused. So this reports a row per sale and the
/// caller reads the outcomes, rather than an all-or-nothing answer that would have to lie.
/// </para>
/// </remarks>
public sealed record PostDesktopSalesToSapCommand(
    Guid CallerUserId,
    IReadOnlyList<string> ExternalReferenceIds
) : IRequest<ErrorOr<DesktopSalesBulkPostResult>>;
